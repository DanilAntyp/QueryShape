using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QueryShape.Reporting;

namespace QueryShape.Cli;

internal static class FindingIdentity
{
    // Exclude checkout roots and line numbers, which change when a patch adds lines.
    internal static string For(string? scope, ScopeReportDiagnosis diagnosis)
    {
        var site = Regex.Replace(diagnosis.CallSite ?? "", @":\d+(?=\s|$)", "", RegexOptions.CultureInvariant);
        var key = JsonSerializer.Serialize(new[] { scope ?? "", diagnosis.RuleId, site,
            string.Join(",", diagnosis.Fingerprints.Order(StringComparer.Ordinal)) });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }

    internal static Dictionary<string, int> Counts(RunMetrics metrics, Func<ScopeReportDiagnosis, bool> predicate) => metrics.Reports
        .SelectMany(r => r.Diagnostics.Where(predicate).Select(d => For(r.Scope, d)))
        .GroupBy(id => id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    internal static int Added(RunMetrics before, RunMetrics after, Func<ScopeReportDiagnosis, bool> predicate)
    {
        var baseline = Counts(before, predicate);
        return Counts(after, predicate).Sum(kv => Math.Max(0, kv.Value - baseline.GetValueOrDefault(kv.Key)));
    }
}
