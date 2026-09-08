using QueryShape.Cli.Commands;
using QueryShape.Reporting;
using QueryShape.Testing;

namespace QueryShape.Cli.Tests;

public class BehaviorVerificationTests
{
    private static RunMetrics Metrics(params ScopeReport[] reports) => RunMetrics.Aggregate(reports);
    private static ScopeReport Report(string name, string? digest = "same") => new(1, name, DateTimeOffset.UtcNow, 1, 1, 1, false, [], [],
        digest is null ? null : new Dictionary<string, string> { ["behavior.v1.result"] = digest }) { CaptureComplete = true };

    [Fact]
    public void Behavior_comparison_checks_all_observations_and_scope_multiplicities()
    {
        var baseline = Metrics(Report("A"), Report("B"), Report("B"));
        BehaviorVerification.Compare(baseline, Metrics(Report("B"), Report("A"), Report("B")), false).Should().BeEmpty();
        BehaviorVerification.Compare(baseline, Metrics(Report("A"), Report("B", "changed"), Report("B")), false).Should().Contain(p => p.Contains("Behavior changed"));
        BehaviorVerification.Compare(baseline, Metrics(Report("A"), Report("B")), false).Should().Contain(p => p.Contains("coverage"));
        BehaviorVerification.Compare(Metrics(Report("A", null)), Metrics(Report("A", null)), false).Should().Contain(p => p.Contains("No behavior observations"));
        BehaviorVerification.Compare(Metrics(Report("A", null)), Metrics(Report("A", null)), true).Should().BeEmpty();
        BehaviorVerification.Compare(Metrics(Report("A")), Metrics(Report("A", "changed")), true).Should().NotBeEmpty();
    }
}

public class ScenarioCommandTests
{
    [Theory]
    [InlineData("scaling", true)]
    [InlineData("scaling", false)]
    [InlineData("reduction", true)]
    [InlineData("reduction", false)]
    public async Task Scenario_reports_render_and_control_exit_status(string kind, bool passes)
    {
        var directory = Path.Combine(Path.GetTempPath(), "qs-scenarios-" + Guid.NewGuid().ToString("N"));
        var json = kind == "scaling"
            ? new ScalingReport("growth", "customers", [new(10, 1, 11, 10, true, [])], passes ? [] : ["commands grow"]).ToJson()
            : new ReductionSummary("reduced", "result", 100, 1, 12, passes, null).ToJson();
        new ScopeReport(1, "summary", DateTimeOffset.UtcNow, 1, 0, 0, false, [], [],
            new Dictionary<string, string> { [kind + ".report"] = json }).WriteTo(directory);
        try
        {
            var output = new StringWriter();
            var code = await new ScenarioCommand { Kind = kind, ReportDirectory = directory, Json = true }
                .ExecuteAsync(output, new StringWriter(), CancellationToken.None);
            code.Should().Be(passes ? 0 : 1);
            using var parsed = System.Text.Json.JsonDocument.Parse(output.ToString());
            parsed.RootElement.GetArrayLength().Should().Be(1);
            output = new StringWriter();
            await new ScenarioCommand { Kind = kind, ReportDirectory = directory }.ExecuteAsync(output, new StringWriter(), CancellationToken.None);
            output.ToString().Should().Contain("QueryShape");
        }
        finally { Directory.Delete(directory, true); }
    }
}
