using System.Text.Json;
using System.Text.Json.Serialization;

namespace QueryShape.Reporting;

/// <summary>One query shape in a <see cref="ScopeReport"/>.</summary>
/// <param name="Fingerprint">Fingerprint.</param>
/// <param name="Shape">Normalized SQL.</param>
/// <param name="Source">Query source.</param>
/// <param name="Count">Executions in the scope.</param>
/// <param name="DurationMs">Summed duration of those executions.</param>
public sealed record ScopeReportQuery(string Fingerprint, string Shape, string Source, int Count, double DurationMs);

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
    string? ManualStep = null);

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
            .Select(g => new ScopeReportQuery(g.Key, g.First().Shape, g.First().Source.ToString(), g.Count(), Math.Round(g.Sum(c => c.Duration.TotalMilliseconds), 3)))
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
            d.SuggestedFix?.ManualStep)).ToArray();

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
            scope.Annotations.Count == 0 ? null : scope.Annotations);
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
