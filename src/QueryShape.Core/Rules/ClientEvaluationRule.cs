namespace QueryShape.Rules;

/// <summary>QS003: user code in the expression tree that EF Core cannot translate and evaluates per row on the client.</summary>
public sealed class ClientEvaluationRule : IRule
{
    /// <summary>Rule id.</summary>
    public const string RuleId = "QS003";

    /// <inheritdoc />
    public string Id => RuleId;

    /// <inheritdoc />
    public string Name => "Client-side evaluation";

    /// <inheritdoc />
    public Severity DefaultSeverity => Severity.Error;

    /// <inheritdoc />
    public IEnumerable<Diagnosis> Analyze(QueryShapeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in scope.Commands.Where(c => c.Source == QuerySource.Linq && c.Query is { ClientEvaluatedCalls.Count: > 0 }).OrderBy(c => c.Sequence))
        {
            var q = c.Query!;
            if (!reported.Add(q.ExpressionHash))
            {
                continue;
            }

            var call = q.ClientEvaluatedCalls[0];
            var root = q.RootEntityShortName ?? "the entity";
            var rowsText = c.RowsReturned is { } r ? RuleHelpers.N(r) + " rows" : "every row";
            var others = q.ClientEvaluatedCalls.Count > 1 ? $" (and {q.ClientEvaluatedCalls.Count - 1} more)" : string.Empty;
            var op = string.IsNullOrEmpty(call.Operator) ? "the query" : "the " + call.Operator;

            var explanation =
                $"EF Core can only translate into SQL the methods it knows: LINQ operators, common string/date/math functions and EF.Functions. " +
                $"{call.Method} is your own code, so there is no SQL for it. Because the call sits in {op}, EF Core does not throw; " +
                $"it silently pulls the columns the method needs to the client and runs {call.Method} once per row after the SQL returns ({rowsText} here). " +
                "Whatever the method does, formatting, filtering or further lookups, now happens in memory and cannot use indexes, and if it touches a navigation " +
                "EF Core may issue an extra query per row. Since EF Core 3.0 this only works in the final Select; the same call in a Where or OrderBy throws.";

            var fix = new Fix(
                $"Move {call.Method} out of the query: project the raw columns, then call it after .AsEnumerable() (or on the materialized list)",
                FixKind.CodeChange,
                q.Expression,
                RuleHelpers.InsertAfterRoot(q.Expression.Split('\n')[0], "Select(x => new { /* raw columns */ })\n    .AsEnumerable()\n    .Select(x => " + call.Method.Split('(')[0] + "(...))"),
                null,
                "Making the client boundary explicit keeps the SQL lean (only the columns you project) and makes it obvious to the next reader that this code runs per row in memory. " +
                "If the database can do the work (string functions, date math), map the method with HasDbFunction or rewrite it with EF.Functions so it translates.",
                scope.Options.DocsUrlFor(RuleId));

            yield return new Diagnosis(
                RuleId,
                DefaultSeverity,
                $"Client-side evaluation: {call.Method}{others} runs in memory for every {root} row{RuleHelpers.AtCallSite(c)}",
                explanation,
                c.CallSite,
                [c.Fingerprint],
                new Evidence(
                    Count: 1,
                    TotalDuration: c.Duration,
                    Rows: c.RowsReturned,
                    SampleSql: c.Shape,
                    SampleExpression: q.Expression,
                    Details: RuleHelpers.Details(
                        ("method", call.Method),
                        ("declaringType", call.DeclaringType),
                        ("operator", call.Operator))),
                fix);
        }
    }
}
