using System.Security;
using System.Text.Json;
using QueryShape.Cli.Commands;
using QueryShape.Reporting;
using QueryShape.Testing;

namespace QueryShape.Cli.Tests;

public class ScenarioFailureTests
{
    [Theory]
    [InlineData(true, 1, "contract-failure")]
    [InlineData(false, 2, "failed")]
    public async Task Failed_test_runs_keep_structured_scenario_evidence(bool contract, int expected, string status)
    {
        var directory = Path.Combine(Path.GetTempPath(), "qs-scenario-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var message = contract ? "QueryShape contract violated: row budget exceeded" : "unrelated application assertion failed";
            var summary = new ScalingReport("operation", "items", [new(10, 1, 2, 50, true, [])], ["row budget exceeded"]);
            var scope = new ScopeReport(1, "summary", DateTimeOffset.UtcNow, 1, 0, 0, false, [], [],
                new Dictionary<string, string> { ["scaling.report"] = summary.ToJson() });
            var trx = $"<TestRun><UnitTestResult testId='stable-id' testName='operation' outcome='Failed'><Output><ErrorInfo><Message>{message}</Message></ErrorInfo></Output></UnitTestResult></TestRun>";
            var project = Path.Combine(directory, "Runner.proj");
            var reportFile = Path.Combine(directory, "source-report.json");
            var trxFile = Path.Combine(directory, "source-results.trx");
            await File.WriteAllTextAsync(reportFile, scope.ToJson());
            await File.WriteAllTextAsync(trxFile, trx);
            await File.WriteAllTextAsync(project, $$"""
                <Project>
                  <Target Name="VSTest">
                    <MakeDir Directories="$(QUERYSHAPE_REPORT_DIR)/test-results" />
                    <Copy SourceFiles="{{SecurityElement.Escape(reportFile)}}" DestinationFiles="$(QUERYSHAPE_REPORT_DIR)/scope.json" />
                    <Copy SourceFiles="{{SecurityElement.Escape(trxFile)}}" DestinationFiles="$(QUERYSHAPE_REPORT_DIR)/test-results/result.trx" />
                    <Error Text="{{message}}" />
                  </Target>
                </Project>
                """);
            var output = new StringWriter();
            var error = new StringWriter();
            var code = await new ScenarioCommand { Kind = "scaling", Project = project, TestFilter = "operation", Json = true }
                .ExecuteAsync(output, error, CancellationToken.None);
            code.Should().Be(expected, error.ToString());
            using var json = JsonDocument.Parse(output.ToString());
            json.RootElement[0].GetProperty("testRunStatus").GetString().Should().Be(status);
            json.RootElement[0].GetProperty("points")[0].GetProperty("rows").GetInt32().Should().Be(50);
            error.ToString().Should().Contain(message);
        }
        finally { Directory.Delete(directory, true); }
    }
}
