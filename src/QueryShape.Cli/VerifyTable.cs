using System.Globalization;
using System.Text;

namespace QueryShape.Cli;

/// <summary>The before/after table. This is the proof `verify` exists for.</summary>
internal static class VerifyTable
{
    public sealed record Result(string Text, bool Improved, int NewErrors, bool BelowNoise);

    public static Result Render(string fixTitle, RunMetrics before, RunMetrics after, IReadOnlyList<RunMetrics> afterRuns)
    {
        var rows = new List<(string Label, string Before, string After, string Delta)>();

        var queryDelta = after.Queries - before.Queries;
        rows.Add(("queries", N(before.Queries), N(after.Queries), Signed(queryDelta)));

        var durationDelta = after.DurationMs - before.DurationMs;
        var durationPct = before.DurationMs > 0 ? durationDelta / before.DurationMs * 100 : 0;
        rows.Add(("duration (ms)", Ms(before.DurationMs), Ms(after.DurationMs), before.DurationMs > 0 ? Pct(durationPct) : Signed((int)Math.Round(durationDelta))));

        var beforeRules = before.RuleCounts;
        var afterRules = after.RuleCounts;
        var anyRuleImproved = false;
        var anyRuleWorse = false;
        foreach (var ruleId in beforeRules.Keys.Union(afterRules.Keys, StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal))
        {
            var b = beforeRules.GetValueOrDefault(ruleId);
            var a = afterRules.GetValueOrDefault(ruleId);
            var name = (before.Diagnostics.Concat(after.Diagnostics).FirstOrDefault(d => d.RuleId == ruleId)?.Title ?? string.Empty).Split(':')[0].Trim();
            if (name.StartsWith(ruleId, StringComparison.Ordinal))
            {
                name = name[ruleId.Length..].Trim();
            }

            var label = (ruleId + " " + Shorten(name, 16)).Trim();
            string delta;
            if (a < b)
            {
                delta = a == 0 ? "✓" : Signed(a - b);
                anyRuleImproved = true;
            }
            else if (a > b)
            {
                delta = "✗ " + Signed(a - b);
                anyRuleWorse = true;
            }
            else
            {
                delta = "=";
            }

            rows.Add((label, N(b), N(a), delta));
        }

        var newErrors = CountNewErrors(before, after);
        rows.Add(("new diagnostics", "-", N(newErrors), newErrors == 0 ? "✓" : "✗"));

        var spread = afterRuns.Count > 1 ? afterRuns.Max(r => r.DurationMs) - afterRuns.Min(r => r.DurationMs) : 0;
        var belowNoise = afterRuns.Count > 1 && Math.Abs(durationDelta) <= spread;

        var improved = newErrors == 0 && !anyRuleWorse && queryDelta <= 0
                       && (queryDelta < 0 || anyRuleImproved || (durationDelta < 0 && !belowNoise));

        var sb = new StringBuilder();
        sb.Append("Fix: ").Append(fixTitle).Append('\n').Append('\n');
        var labelWidth = Math.Max(16, rows.Max(r => r.Label.Length));
        sb.Append(string.Empty.PadRight(labelWidth)).Append("  before     after         Δ").Append('\n');
        foreach (var (label, b, a, d) in rows)
        {
            sb.Append(label.PadRight(labelWidth)).Append("  ").Append(b.PadLeft(6)).Append("  ").Append(a.PadLeft(8)).Append("  ").Append(d.PadLeft(8)).Append('\n');
        }

        if (afterRuns.Count > 1)
        {
            sb.Append('\n').Append(CultureInfo.InvariantCulture, $"after = median of {afterRuns.Count} runs (spread {Ms(spread)} ms)");
            if (belowNoise)
            {
                sb.Append("; note: the duration delta is within run-to-run noise, judge by queries and diagnostics");
            }

            sb.Append('\n');
        }

        sb.Append('\n').Append(improved ? "verdict: improved" : "verdict: not improved").Append('\n');
        return new Result(sb.ToString(), improved, newErrors, belowNoise);
    }

    /// <summary>Error-level (ruleId) occurrences in <paramref name="after"/> beyond those in <paramref name="before"/>.</summary>
    internal static int CountNewErrors(RunMetrics before, RunMetrics after)
    {
        var beforeErrors = before.Diagnostics.Where(d => d.Severity == "Error").GroupBy(d => d.RuleId).ToDictionary(g => g.Key, g => g.Count());
        var newErrors = 0;
        foreach (var g in after.Diagnostics.Where(d => d.Severity == "Error").GroupBy(d => d.RuleId))
        {
            newErrors += Math.Max(0, g.Count() - beforeErrors.GetValueOrDefault(g.Key));
        }

        return newErrors;
    }

    private static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Ms(double ms) => ms.ToString(ms >= 100 ? "N0" : "0.#", CultureInfo.InvariantCulture);

    private static string Signed(int n) => n > 0 ? "+" + N(n) : n == 0 ? "0" : "-" + N(-n);

    private static string Pct(double pct) => (pct > 0 ? "+" : string.Empty) + pct.ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
