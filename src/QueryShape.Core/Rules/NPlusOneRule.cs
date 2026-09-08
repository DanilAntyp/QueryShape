namespace QueryShape.Rules;

/// <summary>QS001: the same query shape executed many times in one scope with different parameters.</summary>
public sealed class NPlusOneRule : IRule
{
    /// <summary>Rule id.</summary>
    public const string RuleId = "QS001";

    /// <inheritdoc />
    public string Id => RuleId;

    /// <inheritdoc />
    public string Name => "N+1 query";

    /// <inheritdoc />
    public Severity DefaultSeverity => Severity.Error;

    /// <inheritdoc />
    public IEnumerable<Diagnosis> Analyze(QueryShapeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var threshold = Math.Max(2, scope.Options.NPlusOneThreshold);
        var all = scope.Commands;

        foreach (var group in RuleHelpers.ReadQueries(scope).GroupBy(c => c.Fingerprint, StringComparer.Ordinal))
        {
            var commands = group.OrderBy(c => c.Sequence).ToList();
            if (commands.Count < threshold)
            {
                continue;
            }

            var distinctParameterSets = commands.Select(c => c.ParameterHash).Distinct(StringComparer.Ordinal).Count();
            if (distinctParameterSets < 2)
            {
                continue; // identical arguments every time: that's QS008, not N+1
            }

            var first = commands[0];
            var count = commands.Count;
            var what = RuleHelpers.Describe(first);
            var rows = commands.Sum(c => (long?)c.RowsReturned ?? 0);

            var explanation = BuildExplanation(first, count, distinctParameterSets);
            var fix = BuildFix(scope, first, all, count);

            yield return new Diagnosis(
                RuleId,
                DefaultSeverity,
                $"N+1 query: {what} executed {count} times{RuleHelpers.AtCallSite(first)}",
                explanation,
                first.CallSite,
                [first.Fingerprint],
                new Evidence(
                    Count: count,
                    TotalDuration: RuleHelpers.Sum(commands),
                    Rows: rows,
                    SampleSql: first.Shape,
                    SampleExpression: first.Query?.Expression,
                    Details: RuleHelpers.Details(
                        ("distinctParameterSets", distinctParameterSets.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        ("threshold", threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)))),
                fix);
        }
    }

    private static string BuildExplanation(CapturedCommand first, int count, int distinctParameterSets)
    {
        var q = first.Query;
        var keyFilter = q?.KeyFilters.FirstOrDefault();

        if (q is { RootEntityShortName: { } root } && keyFilter is not null)
        {
            var parent = keyFilter.RelatedEntityType ?? "parent";
            var relation = keyFilter.IsPrimaryKey
                ? $"loads one {root} by its key, presumably for each {parent} you already have"
                : $"loads the {root} rows for one {parent} at a time (filtering on {root}.{keyFilter.PropertyName})";
            return
                $"EF Core translates each LINQ query into exactly one SQL statement at the moment it is enumerated, and it cannot see the loop around it. " +
                $"This query {relation}; it ran {count} times in this scope with {distinctParameterSets} different parameter values, " +
                $"so the database was asked {count} separate questions instead of one. Every extra round trip adds network latency on top of the query itself, " +
                $"and the total grows with the number of {parent} rows, so a page that is fast with 10 rows becomes slow with 1,000.";
        }

        if (q is not null)
        {
            return
                $"EF Core translates each LINQ query into exactly one SQL statement at the moment it is enumerated, and it cannot see the loop around it. " +
                $"The same query shape ran {count} times in this scope with {distinctParameterSets} different parameter values, which is the signature of a loop " +
                $"that fetches one item per iteration. Each round trip costs latency, so the total time grows with the number of iterations.";
        }

        return
            $"The same SQL statement ran {count} times in this scope with {distinctParameterSets} different parameter values, which is the signature of a loop " +
            $"that fetches one item per iteration. Each round trip costs network latency, so the total time grows with the number of iterations. " +
            "Because this is raw SQL, QueryShape cannot see the LINQ that produced it; the fix is to fetch all the items with a single statement.";
    }

    private static Fix BuildFix(QueryShapeScope scope, CapturedCommand repeated, IReadOnlyList<CapturedCommand> all, int count)
    {
        var docs = scope.Options.DocsUrlFor(RuleId);
        var q = repeated.Query;

        if (q is { RootEntityShortName: { } root })
        {
            // Prefer a key filter whose related entity was actually loaded earlier in the scope (that query is where the Include goes);
            // fall back to any navigation-bearing filter.
            var candidates = q.KeyFilters.Where(kf => kf.RelatedEntityType is not null && kf.NavigationOnRelated is not null).ToList();
            var chosen = candidates
                .Select(kf => (Filter: kf, Parent: all
                    .Where(c => c.Sequence < repeated.Sequence && c.Source == QuerySource.Linq && c.Query?.RootEntityShortName == kf.RelatedEntityType)
                    .OrderByDescending(c => c.Sequence)
                    .FirstOrDefault()))
                .OrderByDescending(x => x.Parent is not null)
                .FirstOrDefault();

            if (chosen.Filter is { } kf)
            {
                var parent = chosen.Parent;
                var p = RuleHelpers.LambdaName(kf.RelatedEntityType!);
                var include = $"Include({p} => {p}.{kf.NavigationOnRelated})";
                var where = parent?.CallSite is { } cs ? $" at {cs}" : parent is not null ? $" (query #{parent.Sequence})" : string.Empty;
                var nav = RuleHelpers.LambdaName(kf.RelatedEntityType!) + "." + kf.NavigationOnRelated;
                var summary = $"Add .{include} to the {kf.RelatedEntityType} query{where}, then read {nav} in the loop instead of querying";

                string? before = parent?.Query?.Expression;
                string? after = before is null ? null : RuleHelpers.InsertAfterRoot(before, include);
                var diff = parent?.CallSite is { FilePath: not null } site
                    ? SourcePatcher.TryInsertBeforeTerminalOperator(site, "." + include)
                    : null;

                var rationale = kf.IsPrimaryKey
                    ? $"With .{include}, EF Core joins {kf.RelatedEntityType} to {root} in the same SQL statement (or a second statement with AsSplitQuery), " +
                      $"so all {count} lookups become part of one query and the loop reads from memory."
                    : $"With .{include}, EF Core loads every {kf.RelatedEntityType} and its {kf.NavigationOnRelated} in one statement (or two with AsSplitQuery). " +
                      $"The {count} per-{kf.RelatedEntityType} queries disappear once the loop reads the loaded navigation instead of running its own query. " +
                      $"If you only need a few columns, project them with Select instead of Include.";

                return new Fix(summary, FixKind.CodeChange, before, after, diff, rationale, docs);
            }

            var pk = q.RootKeyProperties.FirstOrDefault() ?? "Id";
            var x = RuleHelpers.LambdaName(root);
            var filterProp = q.KeyFilters.FirstOrDefault()?.PropertyName ?? pk;
            return new Fix(
                $"Load all {root} rows in one query: collect the keys first, then .Where({x} => keys.Contains({x}.{filterProp}))",
                FixKind.CodeChange,
                q.Expression,
                RuleHelpers.InsertAfterRoot($"DbSet<{root}>()", $"Where({x} => keys.Contains({x}.{filterProp}))"),
                null,
                $"One query with an IN list (or a join) replaces {count} round trips. If the values come from another entity's navigation, prefer .Include on that query.",
                docs);
        }

        return new Fix(
            $"Replace the {count} per-item statements with one statement that fetches all items (IN list, join, or a batched query)",
            FixKind.CodeChange,
            null,
            null,
            null,
            "One round trip with a set-based filter costs roughly the same as one of the current statements; the other " + (count - 1) + " disappear.",
            docs);
    }
}
