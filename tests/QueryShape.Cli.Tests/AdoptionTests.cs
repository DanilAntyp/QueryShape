using System.Text.Json;
using QueryShape.Cli.Commands;
using QueryShape.Reporting;

namespace QueryShape.Cli.Tests;

public class AdoptionTests
{
    private static ScopeReport Scope(string name, int commands, long rows, double ms, params ScopeReportDiagnosis[] diagnoses) =>
        new(1, name, DateTimeOffset.UtcNow, ms, commands, ms, false, [], diagnoses)
        { CaptureComplete = true, RowsReturned = rows };
    private static ScopeReportDiagnosis Finding(string site = "Service.cs:10 Service.List") =>
        new("QS004", "Warning", "full list", "why", site, "Service.cs", 10, ["abc"], null, null, null, null, null, null, null, null);

    [Fact]
    public void Fewer_commands_cannot_hide_slower_execution_or_more_returned_rows()
    {
        var before = RunMetrics.Aggregate([Scope("operation", 10, 10, 10)]);
        var slow = RunMetrics.Aggregate([Scope("operation", 1, 10, 100)]);
        VerifyTable.Render("slow", before, slow, [slow]).Improved.Should().BeFalse();
        var tooManyRows = RunMetrics.Aggregate([Scope("operation", 1, 1000, 5)]);
        VerifyTable.Render("rows", before, tooManyRows, [tooManyRows]).Improved.Should().BeFalse();
    }

    [Fact]
    public void Explicit_budget_can_allow_a_faster_split_query()
    {
        var before = RunMetrics.Aggregate([Scope("operation", 1, 1000, 100)]);
        var after = RunMetrics.Aggregate([Scope("operation", 3, 30, 10)]);
        VerifyTable.Render("split", before, after, [after]).Improved.Should().BeFalse();
        VerifyTable.Render("split", before, after, [after], policy: new VerificationPolicy { MaxCommands = 3 }).Improved.Should().BeTrue();
        new VerificationPolicy { MaxCommands = 3, MaxDurationMs = 5 }.Check(before, after).Should().Contain(s => s.Contains("duration"));
    }

    [Fact]
    public void Totals_and_rule_counts_cannot_hide_a_regression_in_another_scope()
    {
        var before = RunMetrics.Aggregate([Scope("A", 10, 10, 10, Finding()), Scope("B", 1, 1, 1)]);
        var after = RunMetrics.Aggregate([Scope("A", 1, 1, 1), Scope("B", 2, 2, 1, Finding())]);
        new VerificationPolicy().Check(before, after).Should().Contain(s => s.Contains("New warning/error"))
            .And.Contain(s => s.Contains("Commands increased in scope: B"));
    }

    [Fact]
    public void Test_identity_comparison_rejects_replacement_with_the_same_count()
    {
        var before = new ProcessResult(0, "", "") { ExecutedTests = 2, ExecutedTestIdentities = ["A", "B"] };
        var after = before with { ExecutedTestIdentities = ["A", "C"] };
        VerifyCommand.SameTests(before, after).Should().BeFalse();
    }

    [Fact]
    public void Acceptances_expire_and_do_not_hide_increased_or_relocated_findings()
    {
        var date = new DateOnly(2026, 9, 8);
        var finding = Finding();
        var id = FindingIdentity.For("catalog", finding);
        FindingIdentity.For("catalog", Finding("Service.cs:200 Service.List")).Should().Be(id);
        var file = new AcceptanceFile(1, [new(id, "Complete dropdown is intentional", date, 1)]);
        var reports = RunMetrics.Aggregate([Scope("catalog", 2, 30, 1, finding, finding), Scope("other", 1, 30, 1, finding)]);
        var applied = FindingAcceptancePolicy.Apply(reports, file, date);
        applied.Diagnostics.Should().HaveCount(2);
        applied.Reports.SelectMany(r => r.Diagnostics).Count(d => d.Disposition == "accepted").Should().Be(1);
        FindingAcceptancePolicy.Apply(reports, file, date.AddDays(1)).Diagnostics.Should().HaveCount(3);
    }

    [Fact]
    public async Task Doctor_distinguishes_a_scope_from_working_instrumentation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qs-doctor-" + Guid.NewGuid().ToString("N"));
        Scope("empty", 0, 0, 0).WriteTo(dir);
        try
        {
            var output = new StringWriter();
            var code = await new SetupCommand { ReportDirectory = dir, Json = true }.DoctorAsync(output, new StringWriter(), CancellationToken.None);
            code.Should().Be(2);
            using var doc = JsonDocument.Parse(output.ToString());
            doc.RootElement.GetProperty("checks").EnumerateArray().Should().Contain(c => c.GetProperty("name").GetString() == "commands" && !c.GetProperty("passed").GetBoolean());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Runner_distinguishes_contract_failures_from_other_test_failures()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "<TestRun><UnitTestResult testId='id' testName='case' outcome='Failed'><Output><ErrorInfo><Message>QueryShape contract violated: too many rows</Message></ErrorInfo></Output></UnitTestResult></TestRun>");
            TestRun.HasOnlyContractFailures([path]).Should().BeTrue();
            TestRun.ReadTestIdentities([path]).Should().HaveCount(1);
            File.WriteAllText(path, "<TestRun><UnitTestResult testId='id' testName='case' outcome='Failed'><Output><ErrorInfo><Message>application assertion failed</Message></ErrorInfo></Output></UnitTestResult></TestRun>");
            TestRun.HasOnlyContractFailures([path]).Should().BeFalse();
        }
        finally { File.Delete(path); }
    }
}
