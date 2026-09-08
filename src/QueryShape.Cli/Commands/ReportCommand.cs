using QueryShape.Reporting;

namespace QueryShape.Cli.Commands;

/// <summary>`queryshape report`: run tests (or read a report directory) and print every diagnosis with its fix.</summary>
internal sealed class ReportCommand
{
    public string? Project { get; init; }

    public string? TestFilter { get; init; }

    public string? ReportDirectory { get; init; }

    public bool Json { get; init; }

    public async Task<int> ExecuteAsync(TextWriter out_, TextWriter err, CancellationToken ct)
    {
        RunMetrics metrics;
        if (ReportDirectory is not null)
        {
            metrics = RunMetrics.Load(ReportDirectory);
        }
        else
        {
            var dir = Path.Combine(Path.GetTempPath(), "queryshape-report", Guid.NewGuid().ToString("N"));
            var (m, process) = await TestRun.RunAsync(Directory.GetCurrentDirectory(), Project, TestFilter, dir, noBuild: false, null, Json ? TextWriter.Null : out_, ct);
            metrics = m;
            if (metrics.Scopes == 0)
            {
                err.WriteLine("report: no QueryShape scope reports were produced (exit code " + process.ExitCode + ").");
                return 2;
            }
        }

        if (Json)
        {
            out_.WriteLine("[" + string.Join(",\n", metrics.Reports.Select(r => r.ToJson())) + "]");
            return metrics.Diagnostics.Any(d => d.Severity == "Error") ? 1 : 0;
        }

        out_.WriteLine();
        foreach (var report in metrics.Reports)
        {
            out_.WriteLine($"== {report.Scope ?? "(unnamed scope)"}: {report.QueryCount} queries, {report.CommandDurationMs.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} ms in the database, {report.Diagnostics.Count} diagnostics");
            foreach (var d in report.Diagnostics.OrderByDescending(d => d.Severity == "Error" ? 2 : d.Severity == "Warning" ? 1 : 0).ThenBy(d => d.RuleId, StringComparer.Ordinal))
            {
                out_.WriteLine($"  {d.RuleId} {d.Severity.ToUpperInvariant()}  {d.Title}");
                if (d.FixSummary is not null)
                {
                    out_.WriteLine($"    fix  {d.FixSummary}");
                }

                if (d.UnifiedDiff is not null)
                {
                    out_.WriteLine("    patch available (use `queryshape verify --patch-from-diagnosis`)");
                }
            }

            out_.WriteLine();
        }

        var errors = metrics.Diagnostics.Count(d => d.Severity == "Error");
        out_.WriteLine($"{metrics.Scopes} scope(s), {metrics.Queries} queries, {metrics.Diagnostics.Count} diagnostics ({errors} errors)");
        return errors > 0 ? 1 : 0;
    }
}
