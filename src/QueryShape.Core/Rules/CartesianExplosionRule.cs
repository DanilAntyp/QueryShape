using System.Globalization;

namespace QueryShape.Rules;

/// <summary>QS002: a single query with two or more collection includes whose row count is a multiple of the distinct root entities.</summary>
public sealed class CartesianExplosionRule : IRule
{
    /// <summary>Rule id.</summary>
    public const string RuleId = "QS002";

    /// <inheritdoc />
    public string Id => RuleId;

    /// <inheritdoc />
    public string Name => "Cartesian explosion";

    /// <inheritdoc />
    public Severity DefaultSeverity => Severity.Error;

    /// <inheritdoc />
    public IEnumerable<Diagnosis> Analyze(QueryShapeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var factor = Math.Max(2, scope.Options.CartesianExplosionFactor);
        var minRows = scope.Options.CartesianMinimumRows;
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in RuleHelpers.ReadQueries(scope).OrderBy(c => c.Sequence))
        {
            var q = c.Query;
            if (q is not { CollectionIncludes.Count: >= 2, SplittingBehavior: "SingleQuery" } || c.RowsReturned is not { } rows || c.DistinctRootsEstimate is not { } roots || roots <= 0)
            {
                continue;
            }

            if (rows < minRows || rows < roots * factor || !reported.Add(q.ExpressionHash))
            {
                continue;
            }

            var root = q.RootEntityShortName ?? "root";
            var includes = string.Join(", ", q.CollectionIncludes);
            var ratio = (double)rows / roots;
            var x = RuleHelpers.LambdaName(root);
            // Single-query loading can be inherited from the context or written here. Saying which changes what the reader should do about it.
            var chosen = q.SplittingIsExplicit;

            yield return new Diagnosis(
                RuleId,
                DefaultSeverity,
                $"Cartesian explosion: {RuleHelpers.N(rows)} rows for {RuleHelpers.N(roots)} {root} entities ({includes}){RuleHelpers.AtCallSite(c)}",
                $"Including two or more collections in one query makes EF Core LEFT JOIN each collection to the root in the same SELECT. " +
                $"The database returns one row for every combination: a {root} with 5 {q.CollectionIncludes[0]} and 4 {q.CollectionIncludes[1]} comes back as 20 rows, not 9. " +
                $"Here {RuleHelpers.N(roots)} {root} entities produced {RuleHelpers.N(rows)} rows ({ratio.ToString("0.#", CultureInfo.InvariantCulture)} per root), " +
                $"and every one of those rows repeats all the {root} columns and the columns of the other collection. EF Core de-duplicates them in memory afterwards, " +
                "so the app pays for transferring and parsing rows that carry no new information, and the multiplication grows with each additional collection." +
                (chosen
                    ? " This query calls AsSingleQuery() itself, so single-query loading was chosen here rather than inherited: the number above is what that choice costs at this data size."
                    : string.Empty),
                c.CallSite,
                [c.Fingerprint],
                new Evidence(
                    Count: 1,
                    TotalDuration: c.Duration,
                    Rows: rows,
                    SampleSql: c.Shape,
                    SampleExpression: q.Expression,
                    Details: RuleHelpers.Details(
                        ("distinctRoots", roots.ToString(CultureInfo.InvariantCulture)),
                        ("rowsPerRoot", ratio.ToString("0.#", CultureInfo.InvariantCulture)),
                        ("collectionIncludes", includes),
                        ("splitting", chosen ? "AsSingleQuery (explicit)" : "SingleQuery (context default)"),
                        ("efCoreWarning", q.Warnings.Contains("MultipleCollectionInclude", StringComparer.Ordinal) ? "MultipleCollectionInclude" : "none"))),
                new Fix(
                    $"Add .AsSplitQuery() to the {root} query so each collection loads with its own SELECT",
                    FixKind.CodeChange,
                    q.Expression,
                    RuleHelpers.InsertAfterRoot(q.Expression, "AsSplitQuery()"),
                    c.CallSite is { FilePath: not null } site && scope.Options.ShouldReadSourceFiles ? SourcePatcher.TryInsertBeforeTerminalOperator(site, ".AsSplitQuery()", q) : null,
                    $"With AsSplitQuery, EF Core issues one SELECT for {root} and one per collection, joined by the root keys: rows are transferred once each. " +
                    "It costs extra round trips and gives up consistency between the statements unless you wrap them in a transaction; if you only need a few fields, " +
                    $"a Select projection ({x} => new {{ {x}.Id, Count = {x}.{q.CollectionIncludes[0]}.Count }}) avoids the includes entirely." +
                    (chosen
                        ? " This chain already calls AsSingleQuery(), so applying the patch reverses a decision someone made deliberately: find out why before accepting it."
                        : string.Empty),
                    scope.Options.DocsUrlFor(RuleId)));
        }
    }
}
