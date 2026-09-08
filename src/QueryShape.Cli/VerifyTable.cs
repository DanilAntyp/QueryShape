using System.Globalization;
using System.Text;

namespace QueryShape.Cli;

/// <summary>The before/after table and the scope of its measured acceptance criteria.</summary>
internal static class VerifyTable
{
    public sealed record Result(string Text, bool Improved, int NewErrors, bool BelowNoise)
    {
        public IReadOnlyList<Row> Rows { get; init; } = [];

        public string FixTitle { get; init; } = string.Empty;

        public IReadOnlyList<string> Notes { get; init; } = [];

        public int Runs { get; init; }
        public IReadOnlyDictionary<string, string> Outcomes { get; init; } = new Dictionary<string, string>();

        /// <summary>GitHub-flavoured Markdown for pull-request comments and step summaries.</summary>
        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.Append("### QueryShape verify: ").Append(Improved ? "improved ✅" : "not improved ❌").Append('\n').Append('\n');
            sb.Append("**Fix:** ").Append(FixTitle).Append('\n').Append('\n');
            sb.Append("| | before | after | Δ |\n|---|---:|---:|---:|\n");
            foreach (var r in Rows)
            {
                sb.Append("| ").Append(r.Label).Append(" | ").Append(r.Before).Append(" | ").Append(r.After).Append(" | ").Append(r.Delta).Append(" |\n");
            }

            if (Runs > 1)
            {
                sb.Append('\n').Append("_after = median of ").Append(Runs.ToString(CultureInfo.InvariantCulture)).Append(" runs_\n");
            }

            foreach (var note in Notes)
            {
                sb.Append('\n').Append("> ").Append(note).Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>Machine-readable form for CI.</summary>
        public string ToJson()
            => System.Text.Json.JsonSerializer.Serialize(new
            {
                fix = FixTitle,
                improved = Improved,
                newErrors = NewErrors,
                durationDeltaBelowNoise = BelowNoise,
                runs = Runs,
                rows = Rows.Select(r => new { r.Label, r.Before, r.After, r.Delta }),
                notes = Notes,
                outcomes = Outcomes,
            }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public sealed record Row(string Label, string Before, string After, string Delta);

    public static Result Render(string fixTitle, RunMetrics before, RunMetrics after, IReadOnlyList<RunMetrics> afterRuns, IReadOnlyList<string>? notes = null, VerificationPolicy? policy = null, double baselineSpread = 0)
    {
        var rows = new List<(string Label, string Before, string After, string Delta)>();

        var queryDelta = after.Queries - before.Queries;
        rows.Add(("queries", N(before.Queries), N(after.Queries), Signed(queryDelta)));

        var durationDelta = after.DurationMs - before.DurationMs;
        var durationPct = before.DurationMs > 0 ? durationDelta / before.DurationMs * 100 : 0;
        rows.Add(("duration (ms)", Ms(before.DurationMs), Ms(after.DurationMs), before.DurationMs > 0 ? Pct(durationPct) : Signed((int)Math.Round(durationDelta))));

        if (before.RowsReturned is { } br && after.RowsReturned is { } ar)
            rows.Add(("returned rows", br.ToString(CultureInfo.InvariantCulture), ar.ToString(CultureInfo.InvariantCulture), (ar - br).ToString("+0;-0;0", CultureInfo.InvariantCulture)));

        var beforeRules = before.RuleCounts;
        var afterRules = after.RuleCounts;
        var anyRuleImproved = false;

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

            }
            else
            {
                delta = "=";
            }

            rows.Add((label, N(b), N(a), delta));
        }

        var newErrors = CountNewErrors(before, after);
        rows.Add(("new diagnostics", "-", N(newErrors), newErrors == 0 ? "✓" : "✗"));

        // Database time is noisy: a delta must beat the run-to-run spread, 10% of the baseline and 1 ms to count as a change.
        var spread = afterRuns.Count > 1 ? afterRuns.Max(r => r.DurationMs) - afterRuns.Min(r => r.DurationMs) : 0;
        var noiseFloor = Math.Max(Math.Max(spread, before.DurationMs * 0.10), 1.0);
        var belowNoise = Math.Abs(durationDelta) <= noiseFloor;

        var policyBlockers = (policy ?? new VerificationPolicy()).Check(before, after, baselineSpread);
        var improved = policyBlockers.Count == 0
            && (queryDelta < 0 || anyRuleImproved || after.RowsReturned < before.RowsReturned || (durationDelta < 0 && !belowNoise));
        notes = (notes ?? []).Concat(policyBlockers).Append("Verdict covers measured metrics and recorded observations only; production latency improvement is not established.").ToArray();

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
            sb.Append('\n');
        }

        if (belowNoise && durationDelta != 0)
        {
            sb.Append("note: the duration delta is within run-to-run noise; judge by queries and diagnostics\n");
        }

        foreach (var note in notes ?? [])
        {
            sb.Append("note: ").Append(note).Append('\n');
        }

        sb.Append('\n').Append(improved ? "verdict: improved" : "verdict: not improved").Append('\n');

        var allNotes = new List<string>();
        if (belowNoise && durationDelta != 0)
        {
            allNotes.Add("the duration delta is within run-to-run noise; judge by queries and diagnostics");
        }

        allNotes.AddRange(notes ?? []);
        return new Result(sb.ToString(), improved, newErrors, belowNoise)
        {
            Rows = rows.Select(r => new Row(r.Label, r.Before, r.After, r.Delta)).ToList(),
            FixTitle = fixTitle,
            Notes = allNotes,
            Runs = afterRuns.Count,
            Outcomes = new Dictionary<string, string>
            {
                ["commands"] = queryDelta < 0 ? "reduced" : queryDelta > 0 ? "increased" : "unchanged",
                ["returnedRows"] = before.RowsReturned is null || after.RowsReturned is null ? "not measured" : after.RowsReturned < before.RowsReturned ? "reduced" : after.RowsReturned > before.RowsReturned ? "increased" : "unchanged",
                ["commandDuration"] = belowNoise ? "change within noise" : durationDelta < 0 ? "reduced beyond noise estimate" : "increased beyond noise estimate",
                ["productionLatency"] = "not established",
                ["budgets"] = policyBlockers.Count == 0 ? "satisfied" : "violated",
            },
        };
    }

    /// <summary>Error-level (ruleId) occurrences in <paramref name="after"/> beyond those in <paramref name="before"/>.</summary>
    internal static int CountNewErrors(RunMetrics before, RunMetrics after)
    {
        return FindingIdentity.Added(before, after, d => d.Severity == "Error");
    }

    private static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Ms(double ms) => ms.ToString(ms >= 100 ? "N0" : "0.#", CultureInfo.InvariantCulture);

    private static string Signed(int n) => n > 0 ? "+" + N(n) : n == 0 ? "0" : "-" + N(-n);

    private static string Pct(double pct) => (pct > 0 ? "+" : string.Empty) + pct.ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
