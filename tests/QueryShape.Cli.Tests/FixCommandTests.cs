using QueryShape.Cli.Commands;
using QueryShape.Cli.Llm;
using QueryShape.Reporting;

namespace QueryShape.Cli.Tests;

public class LlmPatchTests
{
    private const string Diff = "--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,3 +1,3 @@\n line\n-old\n+new\n line\n";

    [Theory]
    [InlineData("--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,3 +1,3 @@\n line\n-old\n+new\n line")]
    [InlineData("Here you go:\n```diff\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,3 +1,3 @@\n line\n-old\n+new\n line\n```\nHope this helps.")]
    [InlineData("```\r\n--- a/src/A.cs\r\n+++ b/src/A.cs\r\n@@ -1,3 +1,3 @@\r\n line\r\n-old\r\n+new\r\n line\r\n```")]
    [InlineData("diff --git a/src/A.cs b/src/A.cs\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,3 +1,3 @@\n line\n-old\n+new\n line\n")]
    public void Extracts_a_unified_diff_from_fenced_or_raw_answers(string answer)
    {
        var diff = LlmPatch.Extract(answer)!;
        diff.Should().EndWith("\n").And.NotContain("\r").And.Contain("--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,3 +1,3 @@\n line\n-old\n+new\n line\n");
        LlmPatch.TouchedPaths(diff).Should().Equal("src/A.cs");
    }

    [Theory]
    [InlineData("")]
    [InlineData("CANNOT")]
    [InlineData("Add .Include(c => c.Orders) to the query.")]
    [InlineData("--- a/src/A.cs\nno hunks here")]
    public void Rejects_answers_without_a_diff(string answer) => LlmPatch.Extract(answer).Should().BeNull();

    [Fact]
    public void Validation_rejects_paths_outside_the_repository_or_missing_files()
    {
        var repo = ScopeReportTests.FindRepoRoot();
        LlmPatch.Validate(Diff.Replace("src/A.cs", "README.md"), repo).Should().BeNull();
        LlmPatch.Validate(Diff.Replace("src/A.cs", "../etc/passwd"), repo).Should().Contain("outside the repository");
        LlmPatch.Validate(Diff.Replace("src/A.cs", "/etc/passwd"), repo).Should().Contain("outside the repository");
        LlmPatch.Validate(Diff, repo).Should().Contain("does not exist");
    }
}

public class FixPromptTests
{
    [Fact]
    public void Prompt_has_numbered_source_lines_and_the_partial_patch()
    {
        var file = Path.Combine(Path.GetTempPath(), $"qs-{Guid.NewGuid():N}.cs");
        File.WriteAllLines(file, ["public async Task Run()", "{", "    var customers = await db.Customers.ToListAsync();", "}"]);
        try
        {
            var d = new ScopeReportDiagnosis("QS001", "Error", "N+1", "why", "X.cs:3", file, 3, [], null, null, "Add .Include", null, null, null,
                "--- a/x\n+++ b/x\n@@ -3,1 +3,1 @@\n-    var customers = await db.Customers.ToListAsync();\n+    var customers = await db.Customers.Include(c => c.Orders).ToListAsync();\n", null, true, "read c.Orders in the loop");
            var user = FixPrompt.Build(d, Path.GetTempPath());
            user.Should().Contain("\"ruleId\": \"QS001\"").And.Contain("\"manualStep\": \"read c.Orders in the loop\"");
            user.Should().Contain("1: public async Task Run()\n2: {\n3:     var customers = await db.Customers.ToListAsync();\n4: }\n");
            user.Should().Contain("## QueryShape's own patch (partial: complete it)");
            FixPrompt.SystemPrompt.Should().Contain("CANNOT").And.Contain("nothing else");
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class FixCommandTests
{
    [Fact]
    public async Task Fix_requires_the_llm_flag_and_an_api_key()
    {
        var err = new StringWriter();
        (await new FixCommand { TestFilter = "x", UseLlm = false }.ExecuteAsync(new StringWriter(), err, CancellationToken.None)).Should().Be(2);
        err.ToString().Should().Contain("pass --llm");

        var previous = Environment.GetEnvironmentVariable(AnthropicLlmClient.ApiKeyVariable);
        Environment.SetEnvironmentVariable(AnthropicLlmClient.ApiKeyVariable, null);
        try
        {
            err = new StringWriter();
            (await new FixCommand { TestFilter = "x", UseLlm = true }.ExecuteAsync(new StringWriter(), err, CancellationToken.None)).Should().Be(2);
            err.ToString().Should().Contain("fix --llm is disabled: set ANTHROPIC_API_KEY");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AnthropicLlmClient.ApiKeyVariable, previous);
        }
    }
}

/// <summary>The full loop with a fake model: the model's diff is applied in a worktree and measured; garbage is rejected before anything runs.</summary>
[Trait("Category", "Slow")]
public class FixEndToEndTests
{
    private const string GoodPatch =
        "--- a/tests/QueryShape.SampleApp/BadEndpoints.cs\n" +
        "+++ b/tests/QueryShape.SampleApp/BadEndpoints.cs\n" +
        "@@ -11,11 +11,11 @@\n" +
        "         // QS001: one query per customer.\n" +
        "         app.MapGet(\"/bad/n-plus-one\", async (ShopDbContext db) =>\n" +
        "         {\n" +
        "-            var customers = await db.Customers.ToListAsync();\n" +
        "+            var customers = await db.Customers.Include(c => c.Orders).ToListAsync();\n" +
        "             var result = new List<object>();\n" +
        "             foreach (var customer in customers)\n" +
        "             {\n" +
        "-                var orders = await db.Orders.Where(o => o.CustomerId == customer.Id).ToListAsync();\n" +
        "+                var orders = customer.Orders;\n" +
        "                 result.Add(new { customer.Name, Orders = orders.Count });\n" +
        "             }\n";

    private static async Task<(int Code, string Out, string Err)> RunAsync(string modelAnswer)
    {
        var repo = ScopeReportTests.FindRepoRoot();
        var previousDir = Directory.GetCurrentDirectory();
        var previousKey = Environment.GetEnvironmentVariable(AnthropicLlmClient.ApiKeyVariable);
        Directory.SetCurrentDirectory(repo);
        Environment.SetEnvironmentVariable(AnthropicLlmClient.ApiKeyVariable, "fake-key");
        var out_ = new StringWriter();
        var err = new StringWriter();
        try
        {
            var code = await new FixCommand
            {
                Project = "tests/QueryShape.SampleApp.Tests",
                TestFilter = "FullyQualifiedName~BadEndpointTests.N_plus_one_is_diagnosed_with_include_fix",
                RuleFilter = "QS001",
                UseLlm = true,
                ShowPrompt = true,
                Runs = 2,
                AllowDirty = true,
                ClientFactory = (_, _) => new FakeLlm(modelAnswer),
            }.ExecuteAsync(out_, err, CancellationToken.None);
            return (code, out_.ToString(), err.ToString());
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDir);
            Environment.SetEnvironmentVariable(AnthropicLlmClient.ApiKeyVariable, previousKey);
        }
    }

    [SkippableFact]
    public async Task A_correct_model_patch_is_proved_by_verify()
    {
        var (code, text, err) = await RunAsync("Sure, here is the fix:\n```diff\n" + GoodPatch + "```\n");
        text.Should().Contain("---- prompt sent to fake-model (user) ----").And.Contain("\"ruleId\": \"QS001\"").And.Contain("## Source: tests/QueryShape.SampleApp/BadEndpoints.cs");
        text.Should().Contain("==== LLM-proposed patch (fake-model) — generated, not yet verified");
        text.Should().Contain("Fix: LLM patch for QS001 (fake-model): Add .Include(c => c.Orders)");
        System.Text.RegularExpressions.Regex.Replace(text, " +", " ").Should().Contain("\nqueries 41 1 -40\n");
        text.Should().Contain("verdict: improved", err);
        code.Should().Be(0, err);
    }

    [SkippableFact]
    public async Task A_declined_or_broken_model_answer_measures_nothing()
    {
        var (code, _, err) = await RunAsync("CANNOT");
        code.Should().Be(2);
        err.Should().Contain("the model declined (CANNOT)");

        (code, _, err) = await RunAsync("--- a/tests/QueryShape.SampleApp/BadEndpoints.cs\n+++ b/tests/QueryShape.SampleApp/BadEndpoints.cs\n@@ -1,1 +1,1 @@\n-this line does not exist\n+neither does this\n");
        code.Should().Be(2);
        err.Should().Contain("the patch does not apply");
    }

    private sealed class FakeLlm(string answer) : ILlmClient
    {
        public string Model => "fake-model";

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct) => Task.FromResult(answer);
    }
}
