using Microsoft.EntityFrameworkCore;
using QueryShape.Cli;
using QueryShape.Cli.Commands;
using QueryShape.Cli.Llm;
using QueryShape.Core.Tests.TestModel;
using QueryShape.Reporting;

namespace QueryShape.Cli.Tests;

public class ScopeReportTests : IDisposable
{
    private readonly SqliteShop _shop = new();

    public void Dispose() => _shop.Dispose();

    [Fact]
    public async Task Report_round_trips_and_contains_no_parameter_values()
    {
        using var scope = QueryShapeScope.Begin("GET /bad/n-plus-one", _shop.Options);
        await using var ctx = _shop.CreateContext();
        var secret = "DE";
        var customers = await ctx.Customers.Where(c => c.Country == secret).ToListAsync();
        foreach (var c in customers)
        {
            await ctx.Orders.Where(o => o.CustomerId == c.Id).ToListAsync();
        }

        var report = ScopeReport.FromScope(scope, scope.Analyze());
        var json = report.ToJson();
        json.Should().NotContain("\"DE\"").And.NotContain("'DE'");
        json.Should().Contain("\"scope\": \"GET /bad/n-plus-one\"").And.Contain("\"ruleId\": \"QS001\"");

        var back = ScopeReport.FromJson(json)!;
        back.QueryCount.Should().Be(6);
        back.Queries.Should().HaveCount(2);
        back.Queries[0].Count.Should().Be(5);
        back.Diagnostics.Should().Contain(d => d.RuleId == "QS001" && d.UnifiedDiff != null && d.FixSummary!.StartsWith("Add .Include(c => c.Orders)"));

        var dir = Path.Combine(Path.GetTempPath(), "qs-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = report.WriteTo(dir);
            var metrics = RunMetrics.Load(dir);
            metrics.Scopes.Should().Be(1);
            metrics.Queries.Should().Be(6);
            metrics.Fingerprints.Should().HaveCount(2);
            metrics.RuleCounts["QS001"].Should().Be(1);
            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Unified_diffs_use_repository_relative_paths()
    {
        var thisFile = Path.GetFullPath("CliUnitTests.cs", AppContext.BaseDirectory);
        QueryShape.Rules.SourcePatcher.RepositoryRelativePath("/definitely/not/in/a/repo/File.cs").Should().Be("definitely/not/in/a/repo/File.cs");
        var repoFile = Path.Combine(FindRepoRoot(), "README.md");
        QueryShape.Rules.SourcePatcher.RepositoryRelativePath(repoFile).Should().Be("README.md");
        _ = thisFile;
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("not in a git repository");
    }
}

public class VerifyTableTests
{
    private static RunMetrics Metrics(int queries, double ms, params (string Rule, string Severity)[] diagnostics)
        => RunMetrics.Aggregate([new ScopeReport(1, "t", DateTimeOffset.UtcNow, ms, queries, ms, false, [],
            diagnostics.Select(d => new ScopeReportDiagnosis(d.Rule, d.Severity, d.Rule + " N+1 query: x", "why", null, null, 0, [], null, null, "fix", null, null, null, null, null)).ToList())]);

    [Fact]
    public void Improved_fix_renders_table_and_verdict()
    {
        var before = Metrics(341, 1820, ("QS001", "Error"), ("QS004", "Warning"));
        var after = Metrics(2, 41, ("QS004", "Warning"));
        var result = VerifyTable.Render("Add .Include(c => c.Orders) at OrderService.cs:42", before, after, [after, Metrics(2, 45, ("QS004", "Warning")), Metrics(2, 39, ("QS004", "Warning"))]);

        result.Improved.Should().BeTrue();
        result.NewErrors.Should().Be(0);
        result.Text.Should().StartWith("Fix: Add .Include(c => c.Orders) at OrderService.cs:42\n\n");
        var compact = System.Text.RegularExpressions.Regex.Replace(result.Text, " +", " ");
        compact.Should().Contain("\n before after Δ\n");
        compact.Should().Contain("\nqueries 341 2 -339\n");
        compact.Should().Contain("\nduration (ms) 1,820 41 -98%\n");
        compact.Should().Contain("\nQS001 N+1 query 1 0 ✓\n");
        compact.Should().Contain("\nQS004 N+1 query 1 1 =\n");
        compact.Should().Contain("\nnew diagnostics - 0 ✓\n");
        result.Text.Should().Contain("after = median of 3 runs");
        result.Text.Should().EndWith("verdict: improved\n");
    }

    [Fact]
    public void New_error_diagnosis_fails_the_verdict()
    {
        var before = Metrics(10, 100, ("QS001", "Error"));
        var after = Metrics(4, 50, ("QS003", "Error"));
        var result = VerifyTable.Render("x", before, after, [after]);
        result.NewErrors.Should().Be(1);
        result.Improved.Should().BeFalse();
        System.Text.RegularExpressions.Regex.Replace(result.Text, " +", " ").Should().Contain("\nnew diagnostics - 1 ✗\n").And.Contain("\nQS003 N+1 query 0 1 ✗ +1\n");
        result.Text.Should().EndWith("verdict: not improved\n");
    }

    [Fact]
    public void Duration_only_change_within_noise_is_not_an_improvement()
    {
        var before = Metrics(10, 100);
        var runs = new[] { Metrics(10, 95), Metrics(10, 80), Metrics(10, 110) };
        var result = VerifyTable.Render("x", before, RunMetrics.Median(runs), runs);
        result.BelowNoise.Should().BeTrue();
        result.Improved.Should().BeFalse();
        result.Text.Should().Contain("within run-to-run noise");
    }
}

public class ExplainPromptTests
{
    [Fact]
    public void Prompt_contains_the_diagnosis_and_only_the_enclosing_method()
    {
        var file = Path.Combine(Path.GetTempPath(), $"qs-{Guid.NewGuid():N}.cs");
        File.WriteAllLines(file,
        [
            "namespace Shop;",
            "",
            "public class OrderService(ShopDb db)",
            "{",
            "    public async Task<int> Unrelated() => await db.Products.CountAsync();",
            "",
            "    public async Task<List<Customer>> GetAll()",
            "    {",
            "        var customers = await db.Customers.ToListAsync();",
            "        foreach (var c in customers)",
            "        {",
            "            c.Orders = await db.Orders.Where(o => o.CustomerId == c.Id).ToListAsync();",
            "        }",
            "",
            "        return customers;",
            "    }",
            "",
            "    public void Secret() { var cs = \"Server=prod;Password=hunter2\"; }",
            "}",
        ]);
        try
        {
            var d = new ScopeReportDiagnosis("QS001", "Error", "N+1 query: Order by CustomerId executed 10 times", "why", "OrderService.cs:12 OrderService.GetAll", file, 12,
                ["abc"], "SELECT ... WHERE CustomerId = @p0", "DbSet<Order>().Where(...)", "Add .Include(c => c.Orders)", "because", "before", "after", null, "https://docs");
            var (system, user) = ExplainCommand.BuildPrompt(d);

            system.Should().Contain("Entity Framework Core");
            user.Should().Contain("\"ruleId\": \"QS001\"").And.Contain("Add .Include(c => c.Orders)");
            user.Should().Contain("public async Task<List<Customer>> GetAll()").And.Contain("return customers;");
            user.Should().NotContain("Unrelated").And.NotContain("hunter2", "only the enclosing method is sent");
            user.Should().Contain("lines 7-16");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Llm_is_disabled_without_the_api_key()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qs-explain-" + Guid.NewGuid().ToString("N"));
        new ScopeReport(1, "t", DateTimeOffset.UtcNow, 1, 1, 1, false, [],
            [new ScopeReportDiagnosis("QS004", "Warning", "Unbounded query", "why", null, null, 0, [], null, null, "fix it", null, null, null, null, null)]).WriteTo(dir);
        var previous = Environment.GetEnvironmentVariable(AnthropicLlmClient.ApiKeyVariable);
        Environment.SetEnvironmentVariable(AnthropicLlmClient.ApiKeyVariable, null);
        try
        {
            var out_ = new StringWriter();
            var err = new StringWriter();
            var code = await new ExplainCommand { ReportDirectory = dir, UseLlm = true }.ExecuteAsync(out_, err, CancellationToken.None);
            code.Should().Be(2);
            err.ToString().Should().Contain("explain --llm is disabled: set ANTHROPIC_API_KEY");
            out_.ToString().Should().Contain("QS004 WARNING  Unbounded query");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AnthropicLlmClient.ApiKeyVariable, previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Llm_output_is_labeled_and_prompt_can_be_shown()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qs-explain-" + Guid.NewGuid().ToString("N"));
        new ScopeReport(1, "t", DateTimeOffset.UtcNow, 1, 1, 1, false, [],
            [new ScopeReportDiagnosis("QS001", "Error", "N+1 query", "why", null, null, 0, [], null, null, "fix it", null, null, null, null, null)]).WriteTo(dir);
        var previous = Environment.GetEnvironmentVariable(AnthropicLlmClient.ApiKeyVariable);
        Environment.SetEnvironmentVariable(AnthropicLlmClient.ApiKeyVariable, "test-key");
        try
        {
            var out_ = new StringWriter();
            var fake = new FakeLlm();
            var code = await new ExplainCommand { ReportDirectory = dir, UseLlm = true, ShowPrompt = true, ClientFactory = (k, m) => fake }.ExecuteAsync(out_, new StringWriter(), CancellationToken.None);
            code.Should().Be(0);
            fake.Calls.Should().Be(1);
            out_.ToString().Should().Contain("---- prompt sent to fake-model (user) ----").And.Contain("\"ruleId\": \"QS001\"");
            out_.ToString().Should().Contain("==== LLM explanation (fake-model) — generated text, not verified by QueryShape").And.Contain("Because loops.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AnthropicLlmClient.ApiKeyVariable, previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class FakeLlm : ILlmClient
    {
        public int Calls { get; private set; }

        public string Model => "fake-model";

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult("Because loops.");
        }
    }
}
