using QueryShape.Rules;

namespace QueryShape.Core.Tests.Rules;

public class UnboundedResultSetRuleTests
{
    [Fact]
    public void Fires_for_query_without_filter_or_limit()
    {
        using var scope = Synthetic.Scope();
        var q = Synthetic.Query("DbSet<Order>()", "Order");
        scope.Add("SELECT * FROM Orders", query: q, rows: 30, callSite: new CallSite("/a/Report.cs", 5, "Report.All"));

        var d = new UnboundedResultSetRule().Analyze(scope).Should().ContainSingle().Subject;
        d.RuleId.Should().Be("QS004");
        d.Severity.Should().Be(Severity.Warning);
        d.Title.Should().Be("Unbounded query: loads every Order row (30 rows) at Report.cs:5 Report.All");
        d.Explanation.Should().Contain("no Where").And.Contain("Orders").And.Contain("change tracker");
        d.SuggestedFix!.Summary.Should().Be("Filter the Order query (.Where) or page it (.OrderBy(o => o.Id).Take(n))");
        d.SuggestedFix.AfterSnippet.Should().StartWith("DbSet<Order>()\n    .Where(o => /* filter */)\n    .OrderBy(o => o.Id)\n    .Take(100)");
    }

    [Fact]
    public void Fires_for_large_result_even_with_filter()
    {
        using var scope = Synthetic.Scope(o => o.UnboundedRowThreshold = 100);
        var q = Synthetic.Query("DbSet<Order>()\n    .Where(o => o.Total > @__min_0)", "Order", hasFilter: true);
        scope.Add("SELECT * FROM Orders WHERE Total > @p", query: q, rows: 5000);

        var d = new UnboundedResultSetRule().Analyze(scope).Should().ContainSingle().Subject;
        d.Title.Should().Be("Large result set: 5,000 rows returned (threshold 100)");
        d.Explanation.Should().Contain("more than the 100-row threshold");
    }

    [Fact]
    public void Does_not_fire_for_filtered_or_limited_small_queries()
    {
        using var scope = Synthetic.Scope();
        scope.Add("SELECT * FROM Orders WHERE Id = @p", query: Synthetic.Query("x", "Order", hasFilter: true), rows: 1);
        scope.Add("SELECT TOP(10) * FROM Orders", query: Synthetic.Query("y", "Order", hasLimit: true), rows: 10);
        scope.Add("SELECT COUNT(*) FROM Orders", query: Synthetic.Query("z", "Order", hasLimit: true), method: Microsoft.EntityFrameworkCore.Diagnostics.DbCommandMethod.ExecuteScalar);
        new UnboundedResultSetRule().Analyze(scope).Should().BeEmpty();
    }

    [Fact]
    public void Reports_each_query_shape_once()
    {
        using var scope = Synthetic.Scope();
        var q = Synthetic.Query("DbSet<Order>()", "Order");
        scope.Add("SELECT * FROM Orders", query: q, rows: 30);
        scope.Add("SELECT * FROM Orders", query: q, rows: 30);
        new UnboundedResultSetRule().Analyze(scope).Should().ContainSingle();
    }

    [Fact]
    public void Raw_commands_are_only_flagged_by_row_count()
    {
        using var scope = Synthetic.Scope(o => o.UnboundedRowThreshold = 10);
        scope.Add("SELECT * FROM Orders", source: QuerySource.Raw, rows: 5);
        new UnboundedResultSetRule().Analyze(scope).Should().BeEmpty();
        scope.Add("SELECT * FROM Customers", source: QuerySource.Raw, rows: 50);
        new UnboundedResultSetRule().Analyze(scope).Should().ContainSingle().Which.Title.Should().StartWith("Large result set: 50 rows");
    }
}
