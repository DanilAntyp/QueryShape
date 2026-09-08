using Microsoft.EntityFrameworkCore;
using QueryShape.Testing.Xunit.V3;

namespace QueryShape.Testing.Xunit.V3.Tests;

public sealed class BudgetTests : IDisposable
{
    private readonly MiniShop _shop = new();

    public void Dispose() => _shop.Dispose();

    [Fact, QueryBudget(MaxQueries = 2, FailOn = Severity.Error)]
    public async Task Attribute_opens_a_scope_the_test_body_runs_in()
    {
        QueryShapeScope.Current.Should().NotBeNull("Before ran in the test's execution context");
        await using var ctx = _shop.Create();
        await ctx.Products.CountAsync(TestContext.Current.CancellationToken);
        QueryShapeScope.Current!.CommandCount.Should().Be(1);
    }

    [Fact]
    public async Task Exceeding_the_budget_fails_after_the_test()
    {
        var attribute = new QueryBudgetAttribute { MaxQueries = 1 };
        var method = typeof(BudgetTests).GetMethod(nameof(Exceeding_the_budget_fails_after_the_test))!;

        attribute.Before(method, null!);   // the adapter does not use the IXunitTest
        await using (var ctx = _shop.Create())
        {
            await ctx.Products.CountAsync(TestContext.Current.CancellationToken);
            await ctx.Products.CountAsync(TestContext.Current.CancellationToken);
        }

        var act = () => attribute.After(method, null!);
        act.Should().Throw<QueryBudgetExceededException>().Which.Violations[0].Should().Be("queries: 2 > 1 allowed");
        QueryShapeScope.Current.Should().BeNull("the attribute disposed its scope");
    }
}
