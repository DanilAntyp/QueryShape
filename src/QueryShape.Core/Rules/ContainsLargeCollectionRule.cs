using System.Globalization;

namespace QueryShape.Rules;

/// <summary>QS007: <c>Contains</c> (IN / OPENJSON / array parameter) over a collection larger than the threshold.</summary>
public sealed class ContainsLargeCollectionRule : IRule
{
    /// <summary>Rule id.</summary>
    public const string RuleId = "QS007";

    /// <inheritdoc />
    public string Id => RuleId;

    /// <inheritdoc />
    public string Name => "Contains on large collection";

    /// <inheritdoc />
    public Severity DefaultSeverity => Severity.Warning;

    /// <inheritdoc />
    public IEnumerable<Diagnosis> Analyze(QueryShapeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var threshold = scope.Options.ContainsCollectionThreshold;
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in RuleHelpers.ReadQueries(scope).OrderBy(c => c.Sequence))
        {
            if (c.MaxCollectionParameterCount is not { } count || count <= threshold)
            {
                continue;
            }

            var key = c.Query?.ExpressionHash ?? c.Fingerprint;
            if (!reported.Add(key))
            {
                continue;
            }

            var root = c.Query?.RootEntityShortName ?? "the table";
            var inline = System.Text.RegularExpressions.Regex.IsMatch(c.Shape, @" IN \((?!\s*SELECT)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && c.Parameters.Count < count;
            var x = RuleHelpers.LambdaName(root);
            var provider = c.ProviderName ?? string.Empty;
            var mechanism = inline
                ? "an inline IN list with one literal per value"
                : provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) ? "a JSON array parameter unpacked with OPENJSON"
                : provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ? "a JSON array parameter unpacked with json_each"
                : provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) || provider.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase) ? "an array parameter compared with = ANY(...)"
                : "a collection parameter";

            yield return new Diagnosis(
                RuleId,
                DefaultSeverity,
                $"Contains over {RuleHelpers.N(count)} values on the {root} query (threshold {RuleHelpers.N(threshold)}){RuleHelpers.AtCallSite(c)}",
                $"Where({x} => list.Contains({x}.Key)) makes EF Core send the whole in-memory list to the database, here {RuleHelpers.N(count)} values as {mechanism}. " +
                "The database has no statistics for values that arrive in a parameter or literal list, so it cannot pick a good plan, the statement text or parameter grows with the list " +
                "(SQL Server refuses more than 2 100 parameters, and every distinct inline list is a new plan to compile and cache), and the values travel over the network on every call. " +
                "Lists this size usually come from data that already lives in the database, which means the filter can be expressed as a join or subquery instead.",
                c.CallSite,
                [c.Fingerprint],
                new Evidence(
                    Count: 1,
                    TotalDuration: c.Duration,
                    Rows: c.RowsReturned,
                    SampleSql: c.Shape,
                    SampleExpression: c.Query?.Expression,
                    Details: RuleHelpers.Details(
                        ("values", count.ToString(CultureInfo.InvariantCulture)),
                        ("threshold", threshold.ToString(CultureInfo.InvariantCulture)),
                        ("mechanism", mechanism))),
                new Fix(
                    "Replace the in-memory list with a query the database can join (subquery on the source table), or process the list in chunks of " + threshold.ToString(CultureInfo.InvariantCulture),
                    FixKind.CodeChange,
                    c.Query?.Expression,
                    c.Query is null ? null : RuleHelpers.InsertAfterRoot(c.Query.Expression.Split('\n')[0], $"Where({x} => DbSet<Source>().Where(...).Select(s => s.Key).Contains({x}.Key))"),
                    null,
                    "A correlated subquery or join lets the optimizer use indexes and statistics on both sides and keeps the statement constant. When the values truly originate on the client " +
                    "(a file, an API call), chunk them (ids.Chunk(500)) or bulk-insert them into a temporary table and join against it.",
                    scope.Options.DocsUrlFor(RuleId)));
        }
    }
}
