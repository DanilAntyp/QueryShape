namespace QueryShape.Rules;

/// <summary>QS011: Take/Skip/First without OrderBy, as reported by EF Core's own compile-time warning. Non-deterministic paging and picks.</summary>
public sealed class RowLimitingWithoutOrderByRule : IRule
{
    /// <summary>Rule id.</summary>
    public const string RuleId = "QS011";

    /// <inheritdoc />
    public string Id => RuleId;

    /// <inheritdoc />
    public string Name => "Row limiting without OrderBy";

    /// <inheritdoc />
    public Severity DefaultSeverity => Severity.Warning;

    /// <inheritdoc />
    public IEnumerable<Diagnosis> Analyze(QueryShapeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in RuleHelpers.ReadQueries(scope).Where(c => c.Source == QuerySource.Linq && c.Query is not null).OrderBy(c => c.Sequence))
        {
            var q = c.Query!;
            var paging = q.Warnings.Contains("RowLimitingOperationWithoutOrderBy", StringComparer.Ordinal);
            var first = q.Warnings.Contains("FirstWithoutOrderByAndFilter", StringComparer.Ordinal);
            if (!paging && !first || !reported.Add(q.ExpressionHash))
            {
                continue;
            }

            var root = q.RootEntityShortName ?? "the entity";
            var pk = q.RootKeyProperties.FirstOrDefault() ?? "Id";
            var x = RuleHelpers.LambdaName(root);
            var orderBy = $"OrderBy({x} => {x}.{pk})";

            var title = paging
                ? $"Non-deterministic paging: Skip/Take without OrderBy on {root}{RuleHelpers.AtCallSite(c)}"
                : $"Arbitrary row: First/Single on {root} without OrderBy or filter{RuleHelpers.AtCallSite(c)}";
            var explanation = paging
                ? "A relational table has no inherent order: without an ORDER BY the database returns rows in whatever order the chosen plan produces, " +
                  "and that order changes with indexes, statistics, parallelism and concurrent writes. Skip/Take on an unordered query therefore pages over a moving target: " +
                  "rows can appear on two pages or on none, and the same request can return different results minutes apart. EF Core translates the query faithfully " +
                  $"(OFFSET/LIMIT with no ORDER BY) and logs RowLimitingOperationWithoutOrderByWarning at compile time; QueryShape picked that warning up for this query."
                : "First/Single without an OrderBy asks the database for \"any one row\": the row returned depends on the query plan and can change between executions " +
                  "and after schema changes. EF Core logs FirstWithoutOrderByAndFilterWarning for this at compile time; QueryShape picked that warning up for this query.";

            yield return new Diagnosis(
                RuleId,
                DefaultSeverity,
                title,
                explanation,
                c.CallSite,
                [c.Fingerprint],
                new Evidence(
                    Count: 1,
                    TotalDuration: c.Duration,
                    Rows: c.RowsReturned,
                    SampleSql: c.Shape,
                    SampleExpression: q.Expression,
                    Details: RuleHelpers.Details(("efCoreWarning", paging ? "RowLimitingOperationWithoutOrderBy" : "FirstWithoutOrderByAndFilter"))),
                new Fix(
                    $"Add .{orderBy} (or an order that matches the UI) before the {(paging ? "Skip/Take" : "First/Single")}",
                    FixKind.CodeChange,
                    q.Expression,
                    RuleHelpers.InsertAfterRoot(q.Expression, orderBy),
                    c.CallSite is { FilePath: not null } site && scope.Options.ShouldReadSourceFiles ? SourcePatcher.TryInsertBeforeRowLimitingOperator(site, "." + orderBy) : null,
                    "An explicit, total order (a unique key or a sort plus the key as tie-breaker) makes paging stable and the picked row deterministic. " +
                    "Order by an indexed column so the database does not have to sort the whole table to find the page.",
                    scope.Options.DocsUrlFor(RuleId)));
        }
    }
}
