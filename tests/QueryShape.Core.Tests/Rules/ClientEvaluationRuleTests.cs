using QueryShape.Rules;

namespace QueryShape.Core.Tests.Rules;

public class ClientEvaluationRuleTests
{
    [Fact]
    public void Fires_once_per_expression_with_user_method_call()
    {
        using var scope = Synthetic.Scope();
        var q = Synthetic.Query(
            "DbSet<Order>()\n    .Select(o => new Dto{ Total = PriceFormatter.Format(o.Total) })", "Order",
            clientCalls: [new ClientEvaluatedCall("PriceFormatter.Format(Decimal)", "Shop.PriceFormatter", "Select")]);
        scope.Add("SELECT Total FROM Orders", query: q, rows: 30, callSite: new CallSite("/a/OrdersController.cs", 12, "OrdersController.List"));
        scope.Add("SELECT Total FROM Orders", query: q, rows: 30);

        var d = new ClientEvaluationRule().Analyze(scope).Should().ContainSingle().Subject;
        d.RuleId.Should().Be("QS003");
        d.Severity.Should().Be(Severity.Error);
        d.Title.Should().Be("Client-side evaluation: PriceFormatter.Format(Decimal) runs in memory for every Order row at OrdersController.cs:12 OrdersController.List");
        d.Explanation.Should().Contain("your own code").And.Contain("once per row").And.Contain("30 rows");
        d.SuggestedFix!.Summary.Should().StartWith("Move PriceFormatter.Format(Decimal) out of the query");
        d.SuggestedFix.AfterSnippet.Should().Contain(".AsEnumerable()");
        d.Evidence.Details!["method"].Should().Be("PriceFormatter.Format(Decimal)");
    }

    [Fact]
    public void Does_not_fire_without_client_calls()
    {
        using var scope = Synthetic.Scope();
        scope.Add("SELECT * FROM Orders", query: Synthetic.Query("DbSet<Order>()", "Order"));
        new ClientEvaluationRule().Analyze(scope).Should().BeEmpty();
    }
}
