using System.Text.Json;

namespace QueryShape.Cli;

internal static class BehaviorVerification
{
    internal static IReadOnlyList<string> Compare(RunMetrics before, RunMetrics after, bool performanceOnly)
    {
        var problems = new List<string>();
        if (before.Reports.Any(r => r.CaptureComplete != true) || after.Reports.Any(r => r.CaptureComplete != true))
            problems.Add("Capture completeness is missing or false; rerun with current instrumentation and finish every reader.");
        if (before.Reports.Any(r => r.Overflowed) || after.Reports.Any(r => r.Overflowed)) problems.Add("Capture overflowed; complete measurements are required.");
        var beforeScopes = before.Reports.Select(r => r.Scope ?? "").Order(StringComparer.Ordinal);
        var afterScopes = after.Reports.Select(r => r.Scope ?? "").Order(StringComparer.Ordinal);
        if (!beforeScopes.SequenceEqual(afterScopes)) problems.Add("Scope coverage changed (missing, added or renamed executions).");
        var expected = Observations(before);
        var actual = Observations(after);
        if (expected.Count == 0 && !performanceOnly) problems.Add("No behavior observations. Record results/state with QueryBehavior.Observe or QueryScenario, or explicitly use --performance-only.");
        // Even performance-only never ignores observations that were actually supplied.
        foreach (var key in expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal))
        {
            if (!expected.TryGetValue(key, out var b) || !actual.TryGetValue(key, out var a) || !b.SequenceEqual(a))
                problems.Add("Behavior changed or observation missing: " + key);
        }
        return problems;
    }

    internal static Dictionary<string, string[]> Observations(RunMetrics metrics) => metrics.Reports
        .SelectMany(r => (r.Annotations ?? new Dictionary<string, string>()).Where(a => a.Key.StartsWith("behavior.v1.", StringComparison.Ordinal))
            .Select(a => (Key: JsonSerializer.Serialize(new[] { r.Scope ?? "", a.Key }), a.Value)))
        .GroupBy(a => a.Key, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Select(a => a.Value).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
}
