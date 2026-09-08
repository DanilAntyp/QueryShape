using QueryShape.Internal;
using QueryShape.Rules;

namespace QueryShape.Core.Tests.Rules;

public class OptionsTests
{
    [Fact]
    public void Test_process_is_detected_and_turns_call_site_capture_on_by_default()
    {
        TestEnvironment.IsTestProcess.Should().BeTrue("xunit.core is loaded here");
        var options = new QueryShapeOptions();
        options.CaptureCallSites.Should().BeTrue();
        options.ReadSourceFiles.Should().BeNull();
        options.ShouldReadSourceFiles.Should().BeTrue();
    }

    [Fact]
    public void Source_reads_follow_call_site_capture_unless_set_explicitly()
    {
        new QueryShapeOptions { CaptureCallSites = false }.ShouldReadSourceFiles.Should().BeFalse();
        new QueryShapeOptions { CaptureCallSites = false, ReadSourceFiles = true }.ShouldReadSourceFiles.Should().BeTrue();
        new QueryShapeOptions { CaptureCallSites = true, ReadSourceFiles = false }.ShouldReadSourceFiles.Should().BeFalse();
    }

    [Fact]
    public void Rules_do_not_read_source_files_when_source_reads_are_off()
    {
        var file = Path.Combine(Path.GetTempPath(), $"qs-{Guid.NewGuid():N}.cs");
        File.WriteAllLines(file, ["var products = await db.Products.ToListAsync();"]);
        try
        {
            var query = Synthetic.Query("DbSet<Product>()", "Product", tracking: true);

            using var off = Synthetic.Scope(o => o.ReadSourceFiles = false);
            off.Add("SELECT * FROM Products", query: query, rows: 3, callSite: new CallSite(file, 1, "X.M"));
            new TrackingOnReadOnlyQueryRule().Analyze(off).Single().SuggestedFix!.UnifiedDiff.Should().BeNull("production must not touch the file system");

            using var on = Synthetic.Scope(o => o.ReadSourceFiles = true);
            on.Add("SELECT * FROM Products", query: query, rows: 3, callSite: new CallSite(file, 1, "X.M"));
            new TrackingOnReadOnlyQueryRule().Analyze(on).Single().SuggestedFix!.UnifiedDiff.Should().Contain("+var products = await db.Products.AsNoTracking().ToListAsync();");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
