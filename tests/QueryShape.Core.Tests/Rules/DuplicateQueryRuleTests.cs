using QueryShape.Rules;

namespace QueryShape.Core.Tests.Rules;

public class DuplicateQueryRuleTests
{
    [Fact]
    public void Fires_for_same_shape_and_same_parameters()
    {
        using var scope = Synthetic.Scope();
        var q = Synthetic.Query("DbSet<Product>()\n    .Where(p => p.Id == @__id_0)", "Product", hasFilter: true);
        scope.Add("SELECT * FROM Products WHERE Id = @p", parameterHash: "same", query: q, callSite: new CallSite("/a/Cart.cs", 10, "Cart.Load"));
        scope.Add("SELECT * FROM Products WHERE Id = @p", parameterHash: "same", query: q, callSite: new CallSite("/a/Pricing.cs", 20, "Pricing.Quote"));
        scope.Add("SELECT * FROM Products WHERE Id = @p", parameterHash: "other", query: q);

        var d = new DuplicateQueryRule().Analyze(scope).Should().ContainSingle().Subject;
        d.RuleId.Should().Be("QS008");
        d.Severity.Should().Be(Severity.Warning);
        d.Title.Should().Be("Identical query executed 2 times: Product from 2 places (Cart.cs:10 Cart.Load, Pricing.cs:20 Pricing.Quote)");
        d.Explanation.Should().Contain("does not cache query results");
        d.Evidence.Details!["wastedExecutions"].Should().Be("1");
        d.SuggestedFix!.Summary.Should().Contain("once");
    }

    [Fact]
    public void Does_not_fire_for_different_parameters()
    {
        using var scope = Synthetic.Scope();
        scope.Add("SELECT * FROM Products WHERE Id = @p", parameterHash: "a");
        scope.Add("SELECT * FROM Products WHERE Id = @p", parameterHash: "b");
        new DuplicateQueryRule().Analyze(scope).Should().BeEmpty();
    }
}
