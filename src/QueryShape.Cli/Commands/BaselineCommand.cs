using System.Text.Json;

namespace QueryShape.Cli.Commands;

internal sealed class BaselineCommand
{
    public required string ReportDirectory { get; init; }
    public required string OutputPath { get; init; }
    public required string Reason { get; init; }
    public required string Expires { get; init; }
    public string? FindingId { get; init; }

    public Task<int> ExecuteAsync(TextWriter output, TextWriter error, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(Reason) || !DateOnly.TryParseExact(Expires, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var expiry) || expiry < DateOnly.FromDateTime(DateTime.UtcNow))
        { error.WriteLine("Supply a reason and a non-expired --expires date (YYYY-MM-DD)."); return Task.FromResult(2); }
        try
        {
            var metrics = RunMetrics.Load(ReportDirectory);
            if (metrics.Scopes == 0 || metrics.Reports.Any(r => r.CaptureComplete != true))
                throw new InvalidDataException("Baseline requires complete scope reports from current instrumentation.");
            var counts = FindingIdentity.Counts(metrics, _ => true).Where(k => FindingId is null || k.Key == FindingId).ToArray();
            if (counts.Length == 0) throw new InvalidDataException("No matching findings to accept.");
            var file = new AcceptanceFile(1, counts.Select(k => new FindingAcceptance(k.Key, Reason, expiry, k.Value)).ToArray());
            using var stream = new FileStream(OutputPath, FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(stream, file, FindingAcceptancePolicy.Json);
            output.WriteLine($"Saved {counts.Length} finding acceptances to {OutputPath}. Review and commit this file; expired or increased findings become active.");
            return Task.FromResult(0);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { error.WriteLine(ex.Message); return Task.FromResult(2); }
    }
}
