using System.Security;
using QueryShape.Cli.Commands;
using QueryShape.Reporting;

namespace QueryShape.Cli.Tests;

[CollectionDefinition("Isolated verify repository", DisableParallelization = true)]
public class IsolatedVerifyCollection;

[Collection("Isolated verify repository")]
public class VerifyBehaviorTests
{
    [Theory]
    [InlineData("same", false, 0)]
    [InlineData("changed", false, 1)]
    [InlineData("same", true, 2)]
    public async Task Verify_requires_preserved_behavior_and_passing_tests_even_when_queries_drop(string afterDigest, bool failAfter, int expected)
    {
        var repo = Path.Combine(Path.GetTempPath(), "qs-verify-behavior-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        var previous = Directory.GetCurrentDirectory();
        try
        {
            await ProcessRunner.GitAsync(repo, "init");
            var report = new ScopeReport(1, "test", DateTimeOffset.UtcNow, 1, 10, 1, false, [], [],
                new Dictionary<string, string> { ["behavior.v1.result"] = "$(Digest)" }) { CaptureComplete = true, RowsReturned = 1 }.ToJson().Replace("\"queryCount\": 10", "\"queryCount\": $(Commands)");
            await File.WriteAllTextAsync(Path.Combine(repo, "state.txt"), "before\n");
            await File.WriteAllTextAsync(Path.Combine(repo, "Runner.proj"), $$"""
                <Project>
                  <Target Name="VSTest">
                    <MakeDir Directories="$(QUERYSHAPE_REPORT_DIR)/test-results" />
                    <WriteLinesToFile File="$(QUERYSHAPE_REPORT_DIR)/test-results/test.trx" Lines="&lt;TestRun&gt;&lt;UnitTestResult testId=&quot;fixture-id&quot; testName=&quot;fixture-case&quot; outcome=&quot;Passed&quot; /&gt;&lt;/TestRun&gt;" Overwrite="true" />
                    <ReadLinesFromFile File="state.txt"><Output TaskParameter="Lines" PropertyName="State" /></ReadLinesFromFile>
                    <PropertyGroup>
                      <Commands>10</Commands><Digest>same</Digest>
                      <Commands Condition="'$(State)' == 'after'">1</Commands>
                      <Digest Condition="'$(State)' == 'after'">{{afterDigest}}</Digest>
                    </PropertyGroup>
                    <WriteLinesToFile File="$(QUERYSHAPE_REPORT_DIR)/scope.json" Lines="{{SecurityElement.Escape(report)}}" Overwrite="true" />
                    {{(failAfter ? "<Error Condition=\"'$(State)' == 'after'\" Text=\"behavior-assertion-failed\" />" : "")}}
                  </Target>
                </Project>
                """);
            await ProcessRunner.GitAsync(repo, "add", ".");
            await ProcessRunner.GitAsync(repo, "-c", "user.name=QueryShape Tests", "-c", "user.email=tests@example.invalid", "commit", "-m", "fixture");
            var patch = Path.Combine(Path.GetTempPath(), "qs-behavior-" + Guid.NewGuid().ToString("N") + ".diff");
            try
            {
                await File.WriteAllTextAsync(patch, "--- a/state.txt\n+++ b/state.txt\n@@ -1 +1 @@\n-before\n+after\n");
                Directory.SetCurrentDirectory(repo);
                var output = new StringWriter();
                var error = new StringWriter();
                var code = await new VerifyCommand { Project = "Runner.proj", TestFilter = "Case", PatchPath = patch, Runs = 2, Format = "json" }
                    .ExecuteAsync(output, error, CancellationToken.None);
                code.Should().Be(expected, error.ToString());
                if (expected != 2)
                {
                    using var parsed = System.Text.Json.JsonDocument.Parse(output.ToString());
                    parsed.RootElement.GetProperty("improved").GetBoolean().Should().Be(expected == 0);
                }
                if (expected == 1) error.ToString().Should().Contain("Behavior changed");
                if (expected == 2) error.ToString().Should().Contain("patched tests failed").And.Contain("behavior-assertion-failed");
                (await File.ReadAllTextAsync(Path.Combine(repo, "state.txt"))).Should().Be("before\n");
            }
            finally { File.Delete(patch); }
        }
        finally { Directory.SetCurrentDirectory(previous); Directory.Delete(repo, true); }
    }
}
