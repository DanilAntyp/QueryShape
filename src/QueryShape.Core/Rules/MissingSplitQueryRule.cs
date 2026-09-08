using System.Globalization;

namespace QueryShape.Rules;

/// <summary>QS006: two or more collection includes in a single query with visible, but not yet explosive, row multiplication.</summary>
public sealed class MissingSplitQueryRule : IRule
{
    /// <summary>Rule id.</summary>
    public const string RuleId = "QS006";

    /// <inheritdoc />
    public string Id => RuleId;

    /// <inheritdoc />
    public string Name => "Missing split query candidate";

    /// <inheritdoc />
    public Severity DefaultSeverity => Severity.Warning;

    /// <inheritdoc />
    public IEnumerable<Diagnosis> Analyze(QueryShapeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var factor = Math.Max(2, scope.Options.CartesianExplosionFactor);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in RuleHelpers.ReadQueries(scope).OrderBy(c => c.Sequence))
        {
            var q = c.Query;
            if (q is not { CollectionIncludes.Count: >= 2, SplittingBehavior: "SingleQuery" } || c.RowsReturned is not { } rows)
            {
                continue;
            }

            var roots = c.DistinctRootsEstimate;
            var explosive = roots is { } r && r > 0 && rows >= scope.Options.CartesianMinimumRows && rows >= r * factor;
            var efWarned = q.Warnings.Contains("MultipleCollectionInclude", StringComparer.Ordinal);
            var multiplied = rows > (roots ?? 1);
            var structural = efWarned && rows >= scope.Options.CartesianMinimumRows; // EF Core flagged it and the result is not tiny, even if we could not see the multiplication
            if (explosive || (!multiplied && !structural) || !reported.Add(q.ExpressionHash))
            {
                continue; // QS002 owns the explosive case; no multiplication (and no EF warning on a real result set) means nothing to split.
            }

            var root = q.RootEntityShortName ?? "root";
            var includes = string.Join(", ", q.CollectionIncludes);
            var rootsText = roots is { } rr ? $" for {RuleHelpers.N(rr)} {root} entities" : string.Empty;

            yield return new Diagnosis(
                RuleId,
                DefaultSeverity,
                $"Split query candidate: {q.CollectionIncludes.Count} collection includes ({includes}) in one query, {RuleHelpers.N(rows)} rows{rootsText}{RuleHelpers.AtCallSite(c)}",
                $"This query includes {q.CollectionIncludes.Count} collections ({includes}) and EF Core loads them with a single SELECT that LEFT JOINs every collection, " +
                $"so the row count is the product of the collection sizes per {root} rather than their sum. Today that is {RuleHelpers.N(rows)} rows{rootsText}, which is tolerable, " +
                "but the multiplication grows with the data: as either collection gets longer the same query turns into a Cartesian explosion (QS002). " +
                "EF Core itself warns about this pattern (MultipleCollectionIncludeWarning) and offers AsSplitQuery as the switch.",
                c.CallSite,
                [c.Fingerprint],
                new Evidence(
                    Count: 1,
                    TotalDuration: c.Duration,
                    Rows: rows,
                    SampleSql: c.Shape,
                    SampleExpression: q.Expression,
                    Details: RuleHelpers.Details(
                        ("collectionIncludes", includes),
                        ("distinctRoots", roots?.ToString(CultureInfo.InvariantCulture) ?? "unknown"),
                        ("efCoreWarning", efWarned ? "MultipleCollectionInclude" : "none"))),
                new Fix(
                    $"Add .AsSplitQuery() to the {root} query (or UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery) for the whole context)",
                    FixKind.CodeChange,
                    q.Expression,
                    RuleHelpers.InsertAfterRoot(q.Expression, "AsSplitQuery()"),
                    c.CallSite is { FilePath: not null } site && scope.Options.ShouldReadSourceFiles ? SourcePatcher.TryInsertBeforeTerminalOperator(site, ".AsSplitQuery()") : null,
                    "Split queries load each collection with its own statement keyed by the root ids, so rows are transferred once. The price is one extra round trip per collection " +
                    "and no snapshot consistency across the statements (wrap in a transaction if that matters). Setting the default on the DbContext makes every multi-include query split.",
                    scope.Options.DocsUrlFor(RuleId)));
        }
    }
}
