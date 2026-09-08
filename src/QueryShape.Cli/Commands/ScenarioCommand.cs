using System.Text.Json;
using QueryShape.Testing;

namespace QueryShape.Cli.Commands;

internal sealed class ScenarioCommand
{
    public required string Kind { get; init; }
    public string? Project { get; init; }
    public string? TestFilter { get; init; }
    public string? ReportDirectory { get; init; }
    public string? Sizes { get; init; }
    public string? OutputDirectory { get; init; }
    public int MaxAttempts { get; init; } = 200;
    public bool Json { get; init; }

    public async Task<int> ExecuteAsync(TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (MaxAttempts < 1 || Sizes is not null && (Sizes.Split(',').Any(s => !int.TryParse(s, out var n) || n < 0) || Sizes.Split(',').Distinct().Count() < 2))
        {
            error.WriteLine("Use nonnegative sizes (at least two distinct values) and a positive attempt budget.");
            return 2;
        }
        RunMetrics metrics;
        ProcessResult? testProcess = null;
        if (ReportDirectory is not null) metrics = RunMetrics.Load(ReportDirectory);
        else
        {
            if (string.IsNullOrWhiteSpace(TestFilter)) { error.WriteLine(Kind + ": select the scenario test with --test."); return 2; }
            var env = new Dictionary<string, string?>
            {
                ["QUERYSHAPE_SIZES"] = Kind == "scaling" ? Sizes : null,
                ["QUERYSHAPE_REDUCTION_ATTEMPTS"] = Kind == "reduction" ? MaxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                ["QUERYSHAPE_REDUCTION_DIR"] = Kind == "reduction" && OutputDirectory is not null ? Path.GetFullPath(OutputDirectory) : null,
            };
            var dir = Path.Combine(Path.GetTempPath(), "queryshape-" + Kind, Guid.NewGuid().ToString("N"));
            var (m, process) = await TestRun.RunAsync(Directory.GetCurrentDirectory(), Project, TestFilter, dir, false, env, Json ? error : output, ct);
            metrics = m;
            testProcess = process;
            if (!process.Success)
            {
                error.WriteLine(process.StdOut + process.StdErr);
                error.WriteLine(Kind + ": scenario tests failed; results are incomplete.");
            }
        }
        var reports = metrics.Reports.Where(r => r.Annotations?.ContainsKey(Kind + ".report") == true)
            .Select(r => r.Annotations![Kind + ".report"]).ToArray();
        if (reports.Length == 0) { error.WriteLine(Kind + ": no scenario reports. Call QueryScaling.RunAsync or QueryReduction.MinimizeAsync in the selected test."); return 2; }
        var passed = true;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var report in reports)
        {
            if (Kind == "scaling")
            {
                var parsed = JsonSerializer.Deserialize<ScalingReport>(report, json)!;
                passed &= parsed.Passed;
                if (!Json) output.Write(parsed.ToText());
            }
            else
            {
                var parsed = JsonSerializer.Deserialize<ReductionSummary>(report, json)!;
                passed &= parsed.Passed;
                if (!Json) output.Write(parsed.ToText());
            }
        }
        if (Json)
        {
            var documents = reports.Select(report =>
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(report)!;
                node["testRunStatus"] = testProcess is null ? "not-run" : testProcess.Success ? "passed" : testProcess.ContractFailuresOnly ? "contract-failure" : "failed";
                return node.ToJsonString();
            });
            output.WriteLine("[" + string.Join(",", documents) + "]");
        }
        if (testProcess is { Success: false }) return testProcess.ContractFailuresOnly && !passed ? 1 : 2;
        return passed ? 0 : 1;
    }
}
