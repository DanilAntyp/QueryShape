using QueryShape.Reporting;

namespace QueryShape.Cli;

/// <summary>What one query shape cost across a run: how often it ran, how many rows it read, and where it was issued from.</summary>
/// <param name="Count">Executions across the run's scopes.</param>
/// <param name="Rows">Rows read, summed; <c>null</c> when any occurrence did not report a count, so partial sums are never compared.</param>
/// <param name="CallSite">First known call site of the shape.</param>
internal sealed record ShapeMetrics(int Count, long? Rows, string? CallSite);

/// <summary>Aggregated metrics of one test run, read from the scope reports it produced.</summary>
internal sealed class RunMetrics
{
    public int Scopes { get; init; }

    public int Queries { get; init; }

    public double DurationMs { get; init; }
    public long? RowsReturned { get; init; }

    /// <summary>Per-shape counts, rows and call sites, keyed by fingerprint.</summary>
    public IReadOnlyDictionary<string, ShapeMetrics> Shapes { get; init; } = new Dictionary<string, ShapeMetrics>(StringComparer.Ordinal);

    public IReadOnlyList<ScopeReportDiagnosis> Diagnostics { get; init; } = [];

    public IReadOnlyList<ScopeReport> Reports { get; init; } = [];

    /// <summary>Diagnoses per rule id.</summary>
    public IReadOnlyDictionary<string, int> RuleCounts
        => Diagnostics.GroupBy(d => d.RuleId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    public static RunMetrics Load(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new RunMetrics();
        }

        var reports = Directory.EnumerateFiles(directory, "*.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => ScopeReport.FromJson(File.ReadAllText(f)))
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();
        return Aggregate(reports);
    }

    public static RunMetrics Aggregate(IReadOnlyList<ScopeReport> reports)
    {
        var shapes = new Dictionary<string, ShapeMetrics>(StringComparer.Ordinal);
        foreach (var q in reports.SelectMany(r => r.Queries))
        {
            var seen = shapes.GetValueOrDefault(q.Fingerprint);
            shapes[q.Fingerprint] = new ShapeMetrics(
                (seen?.Count ?? 0) + q.Count,
                // One unmeasured occurrence makes the whole sum unusable as a comparison.
                seen is null ? q.RowsReturned : seen.Rows is { } known && q.RowsReturned is { } more ? known + more : null,
                seen?.CallSite ?? q.CallSite);
        }

        return new RunMetrics
        {
            Scopes = reports.Count,
            Queries = reports.Sum(r => r.QueryCount),
            DurationMs = reports.Sum(r => r.CommandDurationMs),
            RowsReturned = reports.All(r => r.RowsReturned.HasValue) ? reports.Sum(r => r.RowsReturned!.Value) : null,
            Shapes = shapes,
            Diagnostics = reports.SelectMany(r => r.Diagnostics).Where(d => d.Disposition != "accepted").ToList(),
            Reports = reports,
        };
    }

    /// <summary>Median of the given runs' durations, with the run whose duration is the median for everything else.</summary>
    public static RunMetrics Median(IReadOnlyList<RunMetrics> runs)
    {
        ArgumentOutOfRangeException.ThrowIfZero(runs.Count);
        var ordered = runs.OrderBy(r => r.DurationMs).ToList();
        var mid = ordered[ordered.Count / 2];
        if (ordered.Count % 2 == 0)
        {
            var lower = ordered[ordered.Count / 2 - 1];
            return new RunMetrics
            {
                Scopes = mid.Scopes,
                Queries = mid.Queries,
                DurationMs = (lower.DurationMs + mid.DurationMs) / 2,
                RowsReturned = mid.RowsReturned,
                Shapes = mid.Shapes,
                Diagnostics = mid.Diagnostics,
                Reports = mid.Reports,
            };
        }

        return mid;
    }
}
