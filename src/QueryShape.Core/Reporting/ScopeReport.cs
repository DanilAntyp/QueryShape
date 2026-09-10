using System.Text.Json;
using System.Text.Json.Serialization;

namespace QueryShape.Reporting;

/// <summary>One query shape in a <see cref="ScopeReport"/>.</summary>
/// <param name="Fingerprint">Fingerprint.</param>
/// <param name="Shape">Normalized SQL.</param>
/// <param name="Source">Query source.</param>
/// <param name="Count">Executions in the scope.</param>
/// <param name="DurationMs">Summed duration of those executions.</param>
public sealed record ScopeReportQuery(string Fingerprint, string Shape, string Source, int Count, double DurationMs)
{
    /// <summary>
    /// Rows read through this shape's readers, summed over its executions in the scope. <c>null</c> when any execution did not report a row count
    /// (a non-reader command, or an older capture), so that a partial sum is never compared as if it were complete.
    /// </summary>
    public long? RowsReturned { get; init; }

    /// <summary>The call site of the first execution, rendered as <c>File.cs:42 Type.Member</c>; <c>null</c> when call-site capture was off or found nothing.</summary>
    public string? CallSite { get; init; }
}

/// <summary>One diagnosis in a <see cref="ScopeReport"/>: everything a tool needs, no parameter values.</summary>
public sealed record ScopeReportDiagnosis(
    string RuleId,
    string Severity,
    string Title,
    string Explanation,
    string? CallSite,
    string? CallSiteFile,
    int CallSiteLine,
    IReadOnlyList<string> Fingerprints,
    string? SampleSql,
    string? SampleExpression,
    string? FixSummary,
    string? FixRationale,
    string? FixBefore,
    string? FixAfter,
    string? UnifiedDiff,
    string? DocsUrl,
    bool FixIsPartial = false,
    string? ManualStep = null)
{
    /// <summary>Evidence category; a detected pattern is not a measured application slowdown.</summary>
    public string Basis => RuleId is "QS011" or "QS_OVERFLOW" ? "Observed pattern" : "Heuristic risk";
    /// <summary>The user frames the query was reached through, innermost first, joined with <c>←</c>; <c>null</c> when only the call site was recorded.</summary>
    public string? CallPath { get; init; }

    public string? FindingId { get; init; }
    public string Disposition { get; init; } = "active";
    public string? AcceptanceReason { get; init; }
    public DateOnly? AcceptanceExpiresOn { get; init; }
}

/// <summary>
/// Machine-readable summary of one completed scope. Written to <c>$QUERYSHAPE_REPORT_DIR/&lt;ticks&gt;-&lt;id&gt;.json</c> when that variable is set;
/// the <c>dotnet queryshape</c> tool reads these files to measure a test run. Contains shapes, counts, timings and diagnoses, never parameter values.
/// </summary>
public sealed record ScopeReport(
    int Version,
    string? Scope,
    DateTimeOffset StartedAt,
    double ElapsedMs,
    int QueryCount,
    double CommandDurationMs,
    bool Overflowed,
    IReadOnlyList<ScopeReportQuery> Queries,
    IReadOnlyList<ScopeReportDiagnosis> Diagnostics,
    IReadOnlyDictionary<string, string>? Annotations = null)
{
    /// <summary>Rows returned through read-command readers, not server-side rows scanned. Null in older captures.</summary>
    public long? RowsReturned { get; init; }
    /// <summary>Whether capture was enabled and all captured commands/readers completed successfully. Null in older captures.</summary>
    public bool? CaptureComplete { get; init; }
    /// <summary>Database providers observed in this scope.</summary>
    public IReadOnlyList<string> Providers { get; init; } = [];
    /// <summary>Current format version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Environment variable naming the directory reports are written to.</summary>
    public const string DirectoryEnvironmentVariable = "QUERYSHAPE_REPORT_DIR";

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Builds a report from a scope and its diagnoses.</summary>
    public static ScopeReport FromScope(QueryShapeScope scope, IReadOnlyList<Diagnosis> diagnoses)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(diagnoses);

        var commands = scope.Commands;
        var queries = commands
            .GroupBy(c => c.Fingerprint, StringComparer.Ordinal)
            .Select(g => new ScopeReportQuery(g.Key, g.First().Shape, g.First().Source.ToString(), g.Count(), Math.Round(g.Sum(c => c.Duration.TotalMilliseconds), 3))
            {
                RowsReturned = g.All(c => c.RowsReturned.HasValue) ? g.Sum(c => (long)c.RowsReturned!.Value) : null,
                CallSite = g.Select(c => c.CallSite).FirstOrDefault(s => s is not null)?.ToString(),
            })
            .OrderByDescending(q => q.Count)
            .ThenBy(q => q.Fingerprint, StringComparer.Ordinal)
            .ToArray();

        var diags = diagnoses.Select(d => new ScopeReportDiagnosis(
            d.RuleId,
            d.Severity.ToString(),
            d.Title,
            d.Explanation,
            d.CallSite?.ToString(),
            d.CallSite?.FilePath,
            d.CallSite?.Line ?? 0,
            d.Fingerprints,
            d.Evidence.SampleSql,
            d.Evidence.SampleExpression,
            d.SuggestedFix?.Summary,
            d.SuggestedFix?.Rationale,
            d.SuggestedFix?.BeforeSnippet,
            d.SuggestedFix?.AfterSnippet,
            d.SuggestedFix?.UnifiedDiff,
            d.SuggestedFix?.DocsUrl,
            d.SuggestedFix?.IsPartial ?? false,
            d.SuggestedFix?.ManualStep)
        {
            CallPath = d.Evidence.CallPath.Count > 1 ? string.Join(" ← ", d.Evidence.CallPath) : null,
        }).ToArray();

        return new ScopeReport(
            CurrentVersion,
            scope.Name,
            scope.StartedAt,
            Math.Round(scope.Elapsed.TotalMilliseconds, 3),
            commands.Count,
            Math.Round(scope.TotalCommandDuration.TotalMilliseconds, 3),
            scope.Overflowed,
            queries,
            diags,
            scope.Annotations.Count == 0 ? null : scope.Annotations)
        {
            Providers = commands.Select(c => c.ProviderName).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            RowsReturned = commands.Where(c => c.Source != QuerySource.SaveChanges && c.ExecuteMethod == Microsoft.EntityFrameworkCore.Diagnostics.DbCommandMethod.ExecuteReader)
                .Sum(c => (long)(c.RowsReturned ?? 0)),
            CaptureComplete = scope.Options.Enabled && !scope.Overflowed && !commands.Any(c => c.Failed)
                && commands.Where(c => c.Source != QuerySource.SaveChanges && c.ExecuteMethod == Microsoft.EntityFrameworkCore.Diagnostics.DbCommandMethod.ExecuteReader).All(c => c.RowsReturned.HasValue),
        };
    }

    /// <summary>Serializes to indented camelCase JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, s_json);

    /// <summary>Parses a report file's content.</summary>
    public static ScopeReport? FromJson(string json) => JsonSerializer.Deserialize<ScopeReport>(json, s_json);

    /// <summary>Writes the report as a new file in <paramref name="directory"/> and returns its path.</summary>
    public string WriteTo(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, ToJson());
        return path;
    }
}

/// <summary>Writes reports when <c>QUERYSHAPE_REPORT_DIR</c> is set. Does nothing otherwise; never throws.</summary>
internal static class ScopeReportWriter
{
    private static readonly Lazy<string?> s_directory = new(() =>
    {
        var value = Environment.GetEnvironmentVariable(ScopeReport.DirectoryEnvironmentVariable);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    });

    public static bool IsEnabled => s_directory.Value is not null;

    public static void TryWrite(QueryShapeScope scope, IReadOnlyList<Diagnosis> diagnoses)
    {
        var dir = s_directory.Value;
        if (dir is null)
        {
            return;
        }

        try
        {
            ScopeReport.FromScope(scope, diagnoses).WriteTo(dir);
        }
        catch (Exception ex)
        {
            Internal.Log.Swallowed(scope.Options, "scope report", ex);
        }
    }
}
