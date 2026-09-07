using System.Globalization;

namespace QueryShape.Rules;

/// <summary>QS004: a query with no filter and no limit, or one that returned more rows than the threshold.</summary>
public sealed class UnboundedResultSetRule : IRule
{
    /// <summary>Rule id.</summary>
    public const string RuleId = "QS004";

    /// <inheritdoc />
    public string Id => RuleId;

    /// <inheritdoc />
    public string Name => "Unbounded result set";

    /// <inheritdoc />
    public Severity DefaultSeverity => Severity.Warning;

    /// <inheritdoc />
    public IEnumerable<Diagnosis> Analyze(QueryShapeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var threshold = scope.Options.UnboundedRowThreshold;
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in RuleHelpers.ReadQueries(scope).OrderBy(c => c.Sequence))
        {
            var q = c.Query;
            var unfiltered = q is not null && !q.IsFromSql && !q.HasFilter && !q.HasLimit;
            var tooManyRows = c.RowsReturned is { } rows && rows > threshold;
            if (!unfiltered && !tooManyRows)
            {
                continue;
            }

            var key = q?.ExpressionHash ?? c.Fingerprint;
            if (!reported.Add(key))
            {
                continue;
            }

            var root = q?.RootEntityShortName ?? "the table";
            var table = q?.RootTableName ?? "the table";
            var rowsText = c.RowsReturned is { } r ? RuleHelpers.N(r) + " rows" : "an unknown number of rows";

            string title;
            string explanation;
            if (unfiltered)
            {
                title = $"Unbounded query: loads every {root} row ({rowsText}){RuleHelpers.AtCallSite(c)}";
                explanation =
                    $"The LINQ query has no Where and no Take/First/Single, so EF Core translates it to a SELECT with no WHERE clause and no row limit; the database returns every row in {table}. " +
                    $"EF Core then materializes each row into a {root}{(q!.IsTracking ? " and registers it in the change tracker, which keeps a snapshot of every property for change detection" : string.Empty)}. " +
                    $"That work is linear in the table size, so a query that is fine with today's {rowsText} becomes the slowest thing in the request as data accumulates, " +
                    "and a large table can exhaust memory outright." +
                    (q.CollectionIncludes.Count > 0 ? $" The Include of {string.Join(", ", q.CollectionIncludes)} multiplies the rows transferred." : string.Empty);
            }
            else
            {
                title = $"Large result set: {rowsText} returned (threshold {RuleHelpers.N(threshold)}){RuleHelpers.AtCallSite(c)}";
                explanation =
                    $"This query returned {rowsText}, more than the {RuleHelpers.N(threshold)}-row threshold. Even with a filter, EF Core materializes every returned row " +
                    $"into an object{(q?.IsTracking == true ? " and tracks it" : string.Empty)}, so the cost of the request is proportional to the size of the result rather than to what the caller actually displays or processes. " +
                    "Results this size usually belong behind paging, an aggregate computed in SQL, or a streaming enumeration.";
            }

            var pk = q?.RootKeyProperties.FirstOrDefault() ?? "Id";
            var x = RuleHelpers.LambdaName(root);
            var before = q?.Expression;
            var page = $"OrderBy({x} => {x}.{pk})\n    .Take({Math.Min(threshold, 100).ToString(CultureInfo.InvariantCulture)})";
            var after = before is null ? null : RuleHelpers.InsertAfterRoot(before, unfiltered ? $"Where({x} => /* filter */)\n    .{page}" : page);

            var fix = new Fix(
                unfiltered
                    ? $"Filter the {root} query (.Where) or page it (.OrderBy({x} => {x}.{pk}).Take(n))"
                    : $"Page the {root} query (.OrderBy({x} => {x}.{pk}).Skip(n).Take(pageSize)) or compute the aggregate in SQL",
                FixKind.CodeChange,
                before,
                after,
                null,
                "A bounded query does a fixed amount of work no matter how large the table grows. If every row really is needed (exports, batch jobs), " +
                "stream with AsAsyncEnumerable() and add AsNoTracking() so memory stays flat; if this is a small lookup table you can suppress this rule for the query.",
                scope.Options.DocsUrlFor(RuleId));

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
                    SampleExpression: q?.Expression,
                    Details: RuleHelpers.Details(
                        ("hasFilter", (q?.HasFilter ?? false).ToString()),
                        ("hasLimit", (q?.HasLimit ?? false).ToString()),
                        ("threshold", threshold.ToString(CultureInfo.InvariantCulture)))),
                fix);
        }
    }
}
