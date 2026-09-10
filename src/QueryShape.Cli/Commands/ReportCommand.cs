using QueryShape.Reporting;

namespace QueryShape.Cli.Commands;

/// <summary>`queryshape report`: run tests (or read a report directory) and print every diagnosis with its fix.</summary>
internal sealed class ReportCommand
{
    public string? Project { get; init; }

    public string? TestFilter { get; init; }

    public string? ReportDirectory { get; init; }

    public bool Json { get; init; }

    public bool Markdown { get; init; }
    public string? Acceptances { get; init; }
    public string FailOn { get; init; } = "error";

    public async Task<int> ExecuteAsync(TextWriter out_, TextWriter err, CancellationToken ct)
    {
        RunMetrics metrics;
        var runFailed = false;
        if (ReportDirectory is not null)
        {
            metrics = RunMetrics.Load(ReportDirectory);
        }
        else
        {
            var dir = Path.Combine(Path.GetTempPath(), "queryshape-report", Guid.NewGuid().ToString("N"));
            var (m, process) = await TestRun.RunAsync(Directory.GetCurrentDirectory(), Project, TestFilter, dir, noBuild: false, null, Json || Markdown ? err : out_, ct);
            metrics = m;
            if (!process.Success)
            {
                runFailed = true;
                err.WriteLine(process.StdOut);
                err.WriteLine(process.StdErr);
                err.WriteLine("report: dotnet test failed (exit code " + process.ExitCode + "); any scope reports below are partial results.");
            }
        }

        if (metrics.Scopes == 0)
        {
            err.WriteLine("report: no QueryShape scope reports were produced. Check the project, test filter, and QueryShape instrumentation.");
            return 2;
        }

        try
        {
            metrics = FindingAcceptancePolicy.Apply(metrics, Acceptances is null ? null : FindingAcceptancePolicy.Load(Acceptances), DateOnly.FromDateTime(DateTime.UtcNow));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { err.WriteLine("report: " + ex.Message); return 2; }
        var threshold = FailOn switch { "info" => 0, "warning" => 1, "error" => 2, _ => -1 };
        if (threshold < 0) { err.WriteLine("report: --fail-on must be info, warning or error."); return 2; }
        var exitCode = runFailed ? 2 : metrics.Diagnostics.Any(d => (d.Severity == "Error" ? 2 : d.Severity == "Warning" ? 1 : 0) >= threshold) ? 1 : 0;
        if (Json)
        {
            out_.WriteLine("[" + string.Join(",\n", metrics.Reports.Select(r => r.ToJson())) + "]");
            return exitCode;
        }

        if (Markdown)
        {
            out_.Write(RenderMarkdown(metrics));
            return exitCode;
        }

        out_.WriteLine();
        WriteSummary(out_, metrics);

        foreach (var report in metrics.Reports)
        {
            var rows = report.RowsReturned is { } r ? $", {r.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} rows" : string.Empty;
            out_.WriteLine($"== {report.Scope ?? "(unnamed scope)"}: {report.QueryCount} queries{rows}, {report.CommandDurationMs.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} ms command execution, {report.Diagnostics.Count} diagnostics");
            foreach (var d in report.Diagnostics.OrderByDescending(d => d.Severity == "Error" ? 2 : d.Severity == "Warning" ? 1 : 0).ThenBy(d => d.RuleId, StringComparer.Ordinal))
            {
                out_.WriteLine($"  {d.RuleId} {d.Severity.ToUpperInvariant()}  {d.Title}");
                out_.WriteLine($"    {d.Basis}; {d.Disposition}; finding {d.FindingId}");
                if (d.AcceptanceReason is not null) out_.WriteLine($"    accepted until {d.AcceptanceExpiresOn}: {d.AcceptanceReason}");
                if (d.FixSummary is not null)
                {
                    out_.WriteLine($"    fix  {d.FixSummary}");
                }

                if (d.UnifiedDiff is not null)
                {
                    out_.WriteLine(d.FixIsPartial
                        ? "    partial patch available; manual step: " + d.ManualStep
                        : "    patch available (use `queryshape verify --patch-from-diagnosis`)");
                }
            }

            out_.WriteLine();
        }

        WriteSummary(out_, metrics);
        out_.WriteLine("Findings describe observed patterns or heuristic risks. Returned rows are not server rows scanned; command time is not endpoint latency.");
        return exitCode;
    }

    /// <summary>
    /// What the run found, before and after the per-scope detail: the totals, then one line per rule with how often it fired, where, and the
    /// worst scope it appeared in. A run over a whole test suite prints many scopes; this is the part a reader can act on without scrolling.
    /// </summary>
    private static void WriteSummary(TextWriter out_, RunMetrics metrics)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var rows = metrics.Reports.All(r => r.RowsReturned.HasValue)
            ? $", {metrics.Reports.Sum(r => r.RowsReturned!.Value).ToString("N0", culture)} rows"
            : string.Empty;
        out_.WriteLine($"{metrics.Scopes} scope(s), {metrics.Queries.ToString("N0", culture)} queries{rows}, {metrics.DurationMs.ToString("0.#", culture)} ms command execution");

        if (metrics.Diagnostics.Count == 0)
        {
            out_.WriteLine("No findings.");
            out_.WriteLine();
            return;
        }

        var bySeverity = new[] { "Error", "Warning", "Info" }
            .Select(s => (Severity: s, Count: metrics.Diagnostics.Count(d => d.Severity == s)))
            .Where(x => x.Count > 0)
            .Select(x => $"{x.Count.ToString("N0", culture)} {x.Severity.ToLowerInvariant()}{(x.Count == 1 || x.Severity == "Info" ? string.Empty : "s")}");
        out_.WriteLine($"{metrics.Diagnostics.Count.ToString("N0", culture)} finding(s): {string.Join(", ", bySeverity)}");

        var groups = metrics.Diagnostics
            .GroupBy(d => d.RuleId, StringComparer.Ordinal)
            .Select(g => (RuleId: g.Key, Rank: g.Max(d => d.Severity == "Error" ? 2 : d.Severity == "Warning" ? 1 : 0), Items: g.ToList()))
            .OrderByDescending(g => g.Rank)
            .ThenByDescending(g => g.Items.Count)
            .ThenBy(g => g.RuleId, StringComparer.Ordinal)
            .ToList();

        var width = groups.Max(g => Subject(g.Items[0].Title).Length);
        foreach (var (ruleId, _, items) in groups)
        {
            var sites = items.Select(d => d.CallSite).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            var where = sites.Count == 0 ? string.Empty
                : "  at " + sites[0] + (sites.Count > 1 ? $" (+{sites.Count - 1} more)" : string.Empty);
            out_.WriteLine($"  {ruleId}  ×{items.Count.ToString(culture).PadRight(3)}{Subject(items[0].Title).PadRight(width)}{where}");
        }

        out_.WriteLine();
    }

    /// <summary>The part of a title before its colon: enough to recognise the rule, short enough to align.</summary>
    private static string Subject(string title)
    {
        var colon = title.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? title[..colon] : title;
    }

    /// <summary>Markdown for step summaries and PR comments: one row per scope, then every diagnosis with its fix.</summary>
    internal static string RenderMarkdown(RunMetrics metrics)
    {
        var sb = new System.Text.StringBuilder();
        var errors = metrics.Diagnostics.Count(d => d.Severity == "Error");
        var warnings = metrics.Diagnostics.Count(d => d.Severity == "Warning");
        sb.Append("### QueryShape report: ").Append(metrics.Queries.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" queries, ")
          .Append(errors.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" error(s), ")
          .Append(warnings.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" warning(s)\n\n");
        sb.Append("| scope | queries | db time (ms) | diagnostics | snapshot |\n|---|---:|---:|---|---|\n");
        foreach (var r in metrics.Reports)
        {
            var diags = r.Diagnostics.Count == 0 ? "–" : string.Join(", ", r.Diagnostics.GroupBy(d => d.RuleId).Select(g => g.Count() > 1 ? g.Key + " ×" + g.Count() : g.Key));
            var snapshot = r.Annotations is not null && r.Annotations.TryGetValue("snapshot.outcome", out var outcome) ? outcome : "–";
            sb.Append("| ").Append(Escape(r.Scope ?? "(unnamed)")).Append(" | ").Append(r.QueryCount).Append(" | ")
              .Append(r.CommandDurationMs.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)).Append(" | ").Append(diags).Append(" | ").Append(snapshot).Append(" |\n");
        }

        var all = metrics.Reports.SelectMany(r => r.Diagnostics.Select(d => (Scope: r.Scope, D: d)))
            .OrderByDescending(x => x.D.Severity == "Error" ? 2 : x.D.Severity == "Warning" ? 1 : 0).ThenBy(x => x.D.RuleId, StringComparer.Ordinal).ToList();
        if (all.Count > 0)
        {
            sb.Append("\n<details><summary>").Append(all.Count).Append(" diagnostic(s)</summary>\n\n");
            foreach (var (scope, d) in all)
            {
                sb.Append("- **").Append(d.RuleId).Append(' ').Append(d.Severity).Append("** ").Append(Escape(d.Title));
                if (scope is not null)
                {
                    sb.Append(" _(").Append(Escape(scope)).Append(")_");
                }

                sb.Append('\n');
                if (d.FixSummary is not null)
                {
                    sb.Append("  - fix: ").Append(Escape(d.FixSummary)).Append(d.FixIsPartial ? " _(partial; manual step: " + Escape(d.ManualStep ?? string.Empty) + ")_" : string.Empty).Append('\n');
                }
                sb.Append("  - ").Append(d.Basis).Append("; ").Append(d.Disposition).Append("; finding `").Append(d.FindingId).Append("`\n");
                if (d.AcceptanceReason is not null) sb.Append("  - accepted until ").Append(d.AcceptanceExpiresOn).Append(": ").Append(Escape(d.AcceptanceReason)).Append('\n');
            }

            sb.Append("\n</details>\n");
        }

        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("|", "\\|", StringComparison.Ordinal);
}
