using System.Security;
using System.Text.Json;
using QueryShape.Cli.Commands;
using QueryShape.Reporting;

namespace QueryShape.Cli.Tests;

public sealed class ReportFailureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "qs-report-failure-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("markdown")]
    public async Task Failed_tests_with_reports_are_partial_results_not_success(string format)
    {
        var project = CreateProject(writeReport: true, fail: true);
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await new ReportCommand { Project = project, Json = format == "json", Markdown = format == "markdown" }
            .ExecuteAsync(output, error, CancellationToken.None);

        code.Should().Be(2);
        error.ToString().Should().Contain("intentional-test-failure").And.Contain("partial results");
        output.ToString().Should().Contain("completed-scope");
        if (format == "json")
        {
            using var json = JsonDocument.Parse(output.ToString());
            json.RootElement.GetArrayLength().Should().Be(1);
        }
    }

    [Fact]
    public async Task Report_surfaces_the_failure_when_no_scopes_were_written()
    {
        var error = new StringWriter();
        var code = await new ReportCommand { Project = CreateProject(writeReport: false, fail: true) }
            .ExecuteAsync(new StringWriter(), error, CancellationToken.None);
        code.Should().Be(2);
        error.ToString().Should().Contain("intentional-test-failure").And.Contain("no QueryShape scope reports");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explain_does_not_claim_no_diagnoses_when_the_run_failed(bool writeReport)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await new ExplainCommand { Project = CreateProject(writeReport, fail: true) }
            .ExecuteAsync(output, error, CancellationToken.None);
        code.Should().Be(2);
        error.ToString().Should().Contain("intentional-test-failure");
        output.ToString().Should().NotContain("No diagnoses to explain.");
    }

    [Fact]
    public async Task Empty_report_directories_are_not_clean_runs()
    {
        Directory.CreateDirectory(_directory);
        (await new ReportCommand { ReportDirectory = _directory }
            .ExecuteAsync(new StringWriter(), new StringWriter(), CancellationToken.None)).Should().Be(2);
        (await new ExplainCommand { ReportDirectory = _directory }
            .ExecuteAsync(new StringWriter(), new StringWriter(), CancellationToken.None)).Should().Be(2);
    }

    [Fact]
    public async Task Successful_test_run_with_a_clean_scope_still_succeeds()
    {
        var project = CreateProject(writeReport: true, fail: false);
        (await new ReportCommand { Project = project }
            .ExecuteAsync(new StringWriter(), new StringWriter(), CancellationToken.None)).Should().Be(0);
        (await new ExplainCommand { Project = project }
            .ExecuteAsync(new StringWriter(), new StringWriter(), CancellationToken.None)).Should().Be(0);
    }

    private string CreateProject(bool writeReport, bool fail)
    {
        Directory.CreateDirectory(_directory);
        var report = new ScopeReport(1, "completed-scope", DateTimeOffset.UtcNow, 1, 0, 0, false, [], []);
        var project = Path.Combine(_directory, "Runner.proj");
        // A dependency-free MSBuild test target exercises the actual dotnet child process and report handoff.
        File.WriteAllText(project, $"""
            <Project>
              <Target Name="VSTest">
                {(writeReport ? "<WriteLinesToFile File=\"$(QUERYSHAPE_REPORT_DIR)/scope.json\" Lines=\"" + SecurityElement.Escape(report.ToJson()) + "\" Overwrite=\"true\" />" : "")}
                {(fail ? "<Error Text=\"intentional-test-failure\" />" : "")}
              </Target>
            </Project>
            """);
        return project;
    }
}
