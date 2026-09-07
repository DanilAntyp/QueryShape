using QueryShape.Reporting;

namespace QueryShape.Cli;

/// <summary>Aggregated metrics of one test run, read from the scope reports it produced.</summary>
internal sealed class RunMetrics
{
    public int Scopes { get; init; }

    public int Queries { get; init; }

    public double DurationMs { get; init; }

    public IReadOnlyDictionary<string, int> Fingerprints { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

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
        var fingerprints = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var q in reports.SelectMany(r => r.Queries))
        {
            fingerprints[q.Fingerprint] = fingerprints.GetValueOrDefault(q.Fingerprint) + q.Count;
        }

        return new RunMetrics
        {
            Scopes = reports.Count,
            Queries = reports.Sum(r => r.QueryCount),
            DurationMs = reports.Sum(r => r.CommandDurationMs),
            Fingerprints = fingerprints,
            Diagnostics = reports.SelectMany(r => r.Diagnostics).ToList(),
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
                Fingerprints = mid.Fingerprints,
                Diagnostics = mid.Diagnostics,
                Reports = mid.Reports,
            };
        }

        return mid;
    }
}
