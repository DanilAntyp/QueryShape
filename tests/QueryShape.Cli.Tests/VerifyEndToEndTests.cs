using QueryShape.Cli.Commands;

namespace QueryShape.Cli.Tests;

/// <summary>
/// Runs the real `verify` flow against this repository: baseline = the sample app's N+1 endpoint test, patch = the Include fix.
/// Slow (builds the solution in a worktree); skipped outside a git checkout or when QUERYSHAPE_SKIP_E2E is set.
/// </summary>
[Trait("Category", "Slow")]
[Collection("Isolated verify repository")]
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
        // The fix the README promises: Include the orders on the customers query and read the navigation in the loop.
        await File.WriteAllTextAsync(patch,
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
            "             }\n" +
            "\n");

        var previousDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(repo);
        var out_ = new StringWriter();
        var err = new StringWriter();
        try
        {
            var code = await new VerifyCommand
            {
                Project = "tests/QueryShape.SampleApp.Tests",
                TestFilter = "FullyQualifiedName~BadEndpointTests.N_plus_one_behavior_is_preserved",
                PatchPath = patch,
                Runs = 3,
                Policy = new VerificationPolicy { AllowedNewWarningRules = ["QS004"] },
                AllowDirty = true,   // local dev loops have uncommitted work; tracked changes are carried into the worktree
            }.ExecuteAsync(out_, err, CancellationToken.None);

            var text = out_.ToString();
            text.Should().Contain("Fix: " + Path.GetFileName(patch), "stderr: " + err);
            var compact = System.Text.RegularExpressions.Regex.Replace(text, " +", " ");
            var targetCount = AssertNPlusOneReduction(text);
            compact.Should().Contain($"\nQS001 N+1 query {targetCount} 0 ✓\n");
            compact.Should().Contain("\nnew diagnostics - 0 ✓\n");
            text.Should().Contain("Behavior observations preserved");
            text.Should().Contain("verdict: improved", err.ToString());
            code.Should().Be(0, err.ToString());
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDir);
            File.Delete(patch);
        }
    }
    internal static int AssertNPlusOneReduction(string text)
    {
        var compact = System.Text.RegularExpressions.Regex.Replace(text, " +", " ");
        var row = System.Text.RegularExpressions.Regex.Match(compact, @"\nqueries (\d+) (\d+) (-\d+)\n");
        row.Success.Should().BeTrue("the verification table must include measured query counts");
        var targets = int.Parse(row.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        targets.Should().BeOneOf(1, 2); // Locally net8 may be skipped; CI executes both native runtimes.
        int.Parse(row.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture).Should().Be(41 * targets);
        int.Parse(row.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture).Should().Be(-40 * targets);
        return targets;
    }
}

[Collection("Isolated verify repository")]
public class VerifyFromDiagnosisEndToEndTests
{
    [SkippableFact]
    [Trait("Category", "Slow")]
    public async Task Verify_with_the_patch_QueryShape_proposed_proves_the_n_plus_one_fix()
    {
        var repo = ScopeReportTests.FindRepoRoot();
        var previousDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(repo);
        var out_ = new StringWriter();
        var err = new StringWriter();
        try
        {
            var code = await new VerifyCommand
            {
                Project = "tests/QueryShape.SampleApp.Tests",
                TestFilter = "FullyQualifiedName~BadEndpointTests.N_plus_one_behavior_is_preserved",
                PatchFromDiagnosis = true,
                RuleFilter = "QS001",
                Runs = 2,
                Policy = new VerificationPolicy { AllowedNewWarningRules = ["QS004"] },
                AllowDirty = true,
            }.ExecuteAsync(out_, err, CancellationToken.None);

            var text = out_.ToString();
            code.Should().Be(0, err.ToString());
            text.Should().NotContain("partial fix", "the sample loop has the shape QueryShape rewrites");
            text.Should().Contain("Fix: Add .Include(c => c.Orders) to the Customer query at BadEndpoints.cs:");
            var compact = System.Text.RegularExpressions.Regex.Replace(text, " +", " ");
            var targetCount = VerifyEndToEndTests.AssertNPlusOneReduction(text);
            compact.Should().Contain($"\nQS001 N+1 query {targetCount} 0 ✓\n");
            text.Should().Contain("verdict: improved", err.ToString());
            code.Should().Be(0, err.ToString());
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDir);
        }
    }
}

public sealed class SkippableFactAttribute : FactAttribute
{
    public SkippableFactAttribute() => Skip = VerifyEndToEndTests.SkipReason;
}
