using QueryShape.Cli.Commands;

namespace QueryShape.Cli.Tests;

/// <summary>
/// Runs the real `verify` flow against this repository: baseline = the sample app's N+1 endpoint test, patch = the Include fix.
/// Slow (builds the solution in a worktree); skipped outside a git checkout or when QUERYSHAPE_SKIP_E2E is set.
/// </summary>
[Trait("Category", "Slow")]
public class VerifyEndToEndTests
{
    public static string? SkipReason
    {
        get
        {
            if (Environment.GetEnvironmentVariable("QUERYSHAPE_SKIP_E2E") is "1" or "true")
            {
                return "QUERYSHAPE_SKIP_E2E is set";
            }

            try
            {
                ScopeReportTests.FindRepoRoot();
                return null;
            }
            catch (InvalidOperationException)
            {
                return "not running inside the QueryShape git repository";
            }
        }
    }

    [SkippableFact]
    public async Task Verify_proves_the_n_plus_one_include_fix_on_the_sample_app()
    {
        var repo = ScopeReportTests.FindRepoRoot();
        var patch = Path.Combine(Path.GetTempPath(), $"qs-fix-{Guid.NewGuid():N}.diff");
        // The fix the README promises: add .Include(c => c.Orders) to the customers query of /bad/n-plus-one.
        await File.WriteAllTextAsync(patch,
            "--- a/tests/QueryShape.SampleApp/BadEndpoints.cs\n" +
            "+++ b/tests/QueryShape.SampleApp/BadEndpoints.cs\n" +
            "@@ -11,7 +11,7 @@\n" +
            "         // QS001: one query per customer.\n" +
            "         app.MapGet(\"/bad/n-plus-one\", async (ShopDbContext db) =>\n" +
            "         {\n" +
            "-            var customers = await db.Customers.ToListAsync();\n" +
            "+            var customers = await db.Customers.Include(c => c.Orders).ToListAsync();\n" +
            "             var result = new List<object>();\n" +
            "             foreach (var customer in customers)\n" +
            "             {\n");

        var previousDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(repo);
        var out_ = new StringWriter();
        var err = new StringWriter();
        try
        {
            var code = await new VerifyCommand
            {
                Project = "tests/QueryShape.SampleApp.Tests",
                TestFilter = "FullyQualifiedName~BadEndpointTests.N_plus_one_is_diagnosed_with_include_fix",
                PatchPath = patch,
                Runs = 3,
            }.ExecuteAsync(out_, err, CancellationToken.None);

            var text = out_.ToString();
            text.Should().Contain("Fix: " + Path.GetFileName(patch));
            text.Should().MatchRegex(@"queries\s+\d+\s+\d+\s+-\d+");
            text.Should().Contain("QS001");
            text.Should().Contain("new diagnostics");
            text.Should().Contain("verdict: improved", err.ToString());
            code.Should().Be(0, err.ToString());
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDir);
            File.Delete(patch);
        }
    }
}

public sealed class SkippableFactAttribute : FactAttribute
{
    public SkippableFactAttribute() => Skip = VerifyEndToEndTests.SkipReason;
}
