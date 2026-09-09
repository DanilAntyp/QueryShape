namespace QueryShape.Rules;

/// <summary>QS005: entities loaded with change tracking in a scope that never saved changes to that entity type.</summary>
public sealed class TrackingOnReadOnlyQueryRule : IRule
{
    /// <summary>Rule id.</summary>
    public const string RuleId = "QS005";

    /// <inheritdoc />
    public string Id => RuleId;

    /// <inheritdoc />
    public string Name => "Tracking on read-only query";

    /// <inheritdoc />
    public Severity DefaultSeverity => Severity.Info;

    /// <inheritdoc />
    public IEnumerable<Diagnosis> Analyze(QueryShapeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var modified = new HashSet<string>(scope.SaveChanges.SelectMany(s => s.ModifiedEntityTypes), StringComparer.Ordinal);
        var modifiedShort = new HashSet<string>(modified.Select(m => m.Contains('.') ? m[(m.LastIndexOf('.') + 1)..] : m), StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in RuleHelpers.ReadQueries(scope).Where(c => c.Source == QuerySource.Linq).OrderBy(c => c.Sequence))
        {
            var q = c.Query;
            if (c.IsTracking != true || q is not { ReturnsEntities: true, RootEntityShortName: { } root })
            {
                continue; // unknown tracking (inferred from another context's compilation, nothing observed) stays silent: no false positives at any severity
            }

            if (c.RowsReturned == 0 && c.TrackedEntities == 0)
            {
                continue; // nothing came back, so nothing was tracked: there is no overhead to report (a lookup that found no row)
            }

            // The query loaded the root and everything it included; saving any of those types means the tracking was used.
            if (q.RootEntityType is { } full && modified.Contains(full) || modifiedShort.Contains(root)
                || q.IncludedEntityTypes.Any(t => modified.Contains(t) || modifiedShort.Contains(ShortName(t)))
                || !reported.Add(q.ExpressionHash))
            {
                continue;
            }

            // A collection include repeats the root per child row. Reader rows are not an entity count.
            var rowsText = q.CollectionIncludes.Count > 0 ? root + " entities and included entities"
                : c.RowsReturned is { } r ? RuleHelpers.N(r) + " " + root + (r == 1 ? " entity" : " entities") : root + " entities";
            var x = RuleHelpers.LambdaName(root);

            yield return new Diagnosis(
                RuleId,
                DefaultSeverity,
                $"Tracked read-only query: {rowsText} loaded with change tracking; no save observed in this scope{RuleHelpers.AtCallSite(c)}",
                $"Queries that return entity types are tracked by default: for every {root} it materializes, EF Core also creates an entry in the change tracker " +
                "with a snapshot of every property so that SaveChanges can detect edits later. This scope never called SaveChanges for " + root +
                ", so tracking may be avoidable on this path. It can add allocations per row and work during later change detection, " +
                "but identity resolution or updates saved outside this scope may require it. Check those uses before changing tracking; no latency improvement is established by this diagnosis." +
                (q.TrackingIsExplicit
                    ? " This query calls AsTracking() itself, so tracking was asked for here rather than inherited from the context: something outside this scope probably relies on it."
                    : string.Empty),
                c.CallSite,
                [c.Fingerprint],
                new Evidence(
                    Count: 1,
                    TotalDuration: c.Duration,
                    Rows: c.RowsReturned,
                    SampleSql: c.Shape,
                    SampleExpression: q.Expression,
                    Details: RuleHelpers.Details(
                        ("saveChangesInScope", scope.SaveChanges.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        ("tracking", q.TrackingIsExplicit ? "AsTracking (explicit)" : "context default"),
                        ("trackedEntitiesObserved", c.TrackedEntities.ToString(System.Globalization.CultureInfo.InvariantCulture)))),
                new Fix(
                    $"Add .AsNoTracking() to the {root} query; for read-mostly contexts set optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking) and opt in with .AsTracking() where you save",
                    FixKind.CodeChange,
                    q.Expression,
                    RuleHelpers.InsertAfterRoot(q.Expression, "AsNoTracking()"),
                    c.CallSite is { FilePath: not null } site && scope.Options.ShouldReadSourceFiles ? SourcePatcher.TryInsertBeforeTerminalOperator(site, ".AsNoTracking()", q) : null,
                    $"No-tracking queries can avoid change-tracker entries and snapshots for {root}; compare results and persisted state before accepting the change. " +
                    $"Use AsNoTrackingWithIdentityResolution() if the same {root} appears several times in the result and you want one instance ({x} references stay consistent).",
                    scope.Options.DocsUrlFor(RuleId)));
        }
    }

    private static string ShortName(string clrName) => clrName.Contains('.') ? clrName[(clrName.LastIndexOf('.') + 1)..] : clrName;
}
