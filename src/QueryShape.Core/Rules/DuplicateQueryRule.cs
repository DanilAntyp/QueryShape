namespace QueryShape.Rules;

/// <summary>QS008: the same shape with the same parameters executed more than once in a scope.</summary>
public sealed class DuplicateQueryRule : IRule
{
    /// <summary>Rule id.</summary>
    public const string RuleId = "QS008";

    /// <inheritdoc />
    public string Id => RuleId;

    /// <inheritdoc />
    public string Name => "Duplicate identical query";

    /// <inheritdoc />
    public Severity DefaultSeverity => Severity.Warning;

    /// <inheritdoc />
    public IEnumerable<Diagnosis> Analyze(QueryShapeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        foreach (var group in RuleHelpers.ReadQueries(scope).GroupBy(c => (c.Fingerprint, c.ParameterHash)))
        {
            var commands = group.OrderBy(c => c.Sequence).ToList();
            if (commands.Count < 2)
            {
                continue;
            }

            var first = commands[0];
            var count = commands.Count;
            var what = RuleHelpers.Describe(first);
            var callSites = commands.Select(c => c.CallSite).Where(c => c is not null).Distinct().ToList();
            var placesText = callSites.Count > 1
                ? $" from {callSites.Count} places ({string.Join(", ", callSites.Select(c => c!.ToString()))})"
                : RuleHelpers.AtCallSite(first);

            var explanation =
                "EF Core does not cache query results. Every time a LINQ query is enumerated, even one identical to the last, EF Core sends the SQL to the database again " +
                $"and materializes the rows again; the change tracker only de-duplicates entity instances after the fact. This query ran {count} times with exactly the same parameters, " +
                $"so {count - 1} of the round trips returned nothing new. This usually comes from calling the same repository/service method twice in one request, " +
                "from an IQueryable that is enumerated more than once (Count() then ToList(), or foreach twice), or from a helper that re-queries something the caller already has.";

            var fix = new Fix(
                $"Run the {what} query once and reuse the result{(callSites.Count > 1 ? " across the call sites" : string.Empty)}",
                FixKind.CodeChange,
                first.Query?.Expression,
                null,
                null,
                "Materialize once (ToListAsync) and pass the result down, or cache it for the lifetime of the request. " +
                "If the two enumerations are Count() and ToList() on the same IQueryable, call ToList() first and use .Count on the list.",
                scope.Options.DocsUrlFor(RuleId));

            yield return new Diagnosis(
                RuleId,
                DefaultSeverity,
                $"Identical query executed {count} times: {what}{placesText}",
                explanation,
                first.CallSite,
                [first.Fingerprint],
                new Evidence(
                    Count: count,
                    TotalDuration: RuleHelpers.Sum(commands),
                    Rows: commands.Sum(c => (long?)c.RowsReturned ?? 0),
                    SampleSql: first.Shape,
                    SampleExpression: first.Query?.Expression,
                    Details: RuleHelpers.Details(("wastedExecutions", (count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))),
                fix);
        }
    }
}
