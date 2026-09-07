using System.Globalization;
using QueryShape.Reporting;

namespace QueryShape.Testing;

/// <summary>Limits a scope must stay within. Unset limits are not checked.</summary>
public sealed class QueryBudget
{
    /// <summary>Maximum number of commands (LINQ, raw and SaveChanges) the scope may record. <c>null</c>: unlimited.</summary>
    public int? MaxQueries { get; init; }

    /// <summary>Maximum total command duration in milliseconds (time spent in the database, summed). <c>null</c>: unlimited.</summary>
    public double? MaxDurationMs { get; init; }

    /// <summary>Any diagnosis at or above this severity fails the budget. <c>null</c>: diagnostics are not checked.</summary>
    public Severity? FailOn { get; init; } = Severity.Warning;

    /// <summary>Evaluates the budget against a scope; returns one line per violation (empty when within budget).</summary>
    public IReadOnlyList<string> Evaluate(QueryShapeScope scope, out IReadOnlyList<Diagnosis> failingDiagnoses)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var violations = new List<string>();

        var count = scope.CommandCount;
        if (MaxQueries is { } max && count > max)
        {
            violations.Add($"queries: {count} > {max} allowed");
        }

        var ms = scope.TotalCommandDuration.TotalMilliseconds;
        if (MaxDurationMs is { } maxMs && ms > maxMs)
        {
            violations.Add($"duration: {ms.ToString("0.#", CultureInfo.InvariantCulture)} ms > {maxMs.ToString("0.#", CultureInfo.InvariantCulture)} ms allowed");
        }

        var failing = new List<Diagnosis>();
        if (FailOn is { } failOn)
        {
            failing.AddRange(scope.Analyze().Where(d => d.Severity >= failOn));
            if (failing.Count > 0)
            {
                violations.Add($"diagnostics: {failing.Count} at or above {failOn} ({string.Join(", ", failing.Select(d => d.RuleId).Distinct())})");
            }
        }

        failingDiagnoses = failing;
        return violations;
    }

    /// <summary>Renders <see cref="Evaluate"/> as a failure message.</summary>
    public string BuildMessage(string testName, QueryShapeScope scope, IReadOnlyList<string> violations, IReadOnlyList<Diagnosis> failingDiagnoses)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(violations);
        ArgumentNullException.ThrowIfNull(failingDiagnoses);

        var sb = new System.Text.StringBuilder();
        sb.Append("QueryShape budget exceeded: ").Append(testName).Append('\n');
        foreach (var v in violations)
        {
            sb.Append("  - ").Append(v).Append('\n');
        }

        var commands = scope.Commands;
        if (MaxQueries is not null && commands.Count > 0)
        {
            sb.Append('\n').Append("Queries in this scope:\n");
            foreach (var group in commands.GroupBy(c => c.Fingerprint).OrderByDescending(g => g.Count()))
            {
                var first = group.First();
                sb.Append("  x").Append(group.Count().ToString(CultureInfo.InvariantCulture)).Append("  ").Append(first.Fingerprint).Append("  ").Append(first.Source).Append("  ");
                sb.Append(first.Shape.Length > 160 ? first.Shape[..157] + "..." : first.Shape).Append('\n');
                if (first.CallSite is not null)
                {
                    sb.Append("        at ").Append(first.CallSite).Append('\n');
                }
            }
        }

        if (failingDiagnoses.Count > 0)
        {
            sb.Append('\n').Append(DiagnosisFormatter.Format(failingDiagnoses, "  "));
        }

        return sb.ToString();
    }
}

/// <summary><c>scope.AssertBudget(new QueryBudget { MaxQueries = 2 })</c>.</summary>
public static class QueryBudgetExtensions
{
    /// <summary>Throws <see cref="QueryBudgetExceededException"/> when the scope exceeds <paramref name="budget"/>.</summary>
    public static void AssertBudget(this QueryShapeScope scope, QueryBudget budget, string? testName = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(budget);
        var violations = budget.Evaluate(scope, out var failing);
        if (violations.Count > 0)
        {
            throw new QueryBudgetExceededException(budget.BuildMessage(testName ?? scope.Name ?? "scope", scope, violations, failing), violations);
        }
    }
}
