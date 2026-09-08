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

            if (q.RootEntityType is { } full && modified.Contains(full) || modifiedShort.Contains(root) || !reported.Add(q.ExpressionHash))
            {
                continue;
            }

            var rowsText = c.RowsReturned is { } r ? RuleHelpers.N(r) + " " + root + (r == 1 ? " entity" : " entities") : root + " entities";
            var x = RuleHelpers.LambdaName(root);

            yield return new Diagnosis(
                RuleId,
                DefaultSeverity,
                $"Tracked read-only query: {rowsText} loaded with change tracking but never modified{RuleHelpers.AtCallSite(c)}",
                $"Queries that return entity types are tracked by default: for every {root} it materializes, EF Core also creates an entry in the change tracker " +
                "with a snapshot of every property so that SaveChanges can detect edits later. This scope never called SaveChanges for " + root +
                ", so that bookkeeping was pure overhead: extra allocations per row, a DetectChanges pass over every tracked entity on the next SaveChanges, " +
                "and identity-resolution lookups on every subsequent query for the same type. On read paths the cost is roughly proportional to the rows loaded.",
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
                        ("trackedEntitiesObserved", c.TrackedEntities.ToString(System.Globalization.CultureInfo.InvariantCulture)))),
                new Fix(
                    $"Add .AsNoTracking() to the {root} query; for read-mostly contexts set optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking) and opt in with .AsTracking() where you save",
                    FixKind.CodeChange,
                    q.Expression,
                    RuleHelpers.InsertAfterRoot(q.Expression, "AsNoTracking()"),
                    c.CallSite is { FilePath: not null } site ? SourcePatcher.TryInsertBeforeTerminalOperator(site, ".AsNoTracking()") : null,
                    $"No-tracking queries skip the change-tracker entry and the property snapshot for each {root}, which is the whole cost. " +
                    $"Use AsNoTrackingWithIdentityResolution() if the same {root} appears several times in the result and you want one instance ({x} references stay consistent).",
                    scope.Options.DocsUrlFor(RuleId)));
        }
    }
}
