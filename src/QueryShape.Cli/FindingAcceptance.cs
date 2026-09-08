using System.Text.Json;
using QueryShape.Reporting;

namespace QueryShape.Cli;

internal sealed record FindingAcceptance(string Id, string Reason, DateOnly ExpiresOn, int MaximumOccurrences = 1);
internal sealed record AcceptanceFile(int Version, IReadOnlyList<FindingAcceptance> Findings);

internal static class FindingAcceptancePolicy
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static AcceptanceFile Load(string path)
    {
        var file = JsonSerializer.Deserialize<AcceptanceFile>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Empty acceptance file.");
        if (file.Version != 1 || file.Findings is null || file.Findings.Any(f => f.Id is null || f.Id.Length != 64 || !f.Id.All(char.IsAsciiHexDigit)
            || string.IsNullOrWhiteSpace(f.Reason) || f.MaximumOccurrences < 1)
            || file.Findings.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != file.Findings.Count)
            throw new InvalidDataException("Acceptance files require version 1, unique finding IDs, reasons, expiry dates and positive occurrence limits.");
        return file;
    }

    internal static RunMetrics Apply(RunMetrics metrics, AcceptanceFile? file, DateOnly today)
    {
        var acceptances = file?.Findings.ToDictionary(f => f.Id, StringComparer.Ordinal) ?? new();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        return RunMetrics.Aggregate(metrics.Reports.Select(r => r with
        {
            Diagnostics = r.Diagnostics.Select(d =>
            {
                var id = FindingIdentity.For(r.Scope, d);
                seen[id] = seen.GetValueOrDefault(id) + 1;
                var applies = acceptances.TryGetValue(id, out var acceptance) && acceptance.ExpiresOn >= today && seen[id] <= acceptance.MaximumOccurrences;
                return d with { FindingId = id, Disposition = applies ? "accepted" : "active",
                    AcceptanceReason = applies ? acceptance!.Reason : null, AcceptanceExpiresOn = applies ? acceptance!.ExpiresOn : null };
            }).ToArray(),
        }).ToArray());
    }
}
