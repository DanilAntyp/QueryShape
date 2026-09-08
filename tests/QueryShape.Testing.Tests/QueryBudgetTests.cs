using System.Reflection;
using Microsoft.EntityFrameworkCore;
using QueryShape.Core.Tests.TestModel;
using QueryShape.Testing;
using QueryShape.Testing.Xunit;

namespace QueryShape.Testing.Tests;

public class QueryBudgetTests : IDisposable
{
    private readonly SqliteShop _shop = new(configure: o => o.UnboundedMinimumRows = 1);   // the five-product lookup table must stay a Warning here

    public void Dispose() => _shop.Dispose();

    [Fact]
    public async Task Budget_violations_are_listed_with_queries()
    {
        using var scope = QueryShapeScope.Begin("budget", _shop.Options);
        await using var ctx = _shop.CreateContext();
        await ctx.Products.ToListAsync();
        await ctx.Products.ToListAsync();
        await ctx.Customers.CountAsync();

        var budget = new QueryBudget { MaxQueries = 2, MaxDurationMs = 0, FailOn = Severity.Warning };
        var act = () => scope.AssertBudget(budget, "QueryBudgetTests.Budget_violations_are_listed_with_queries");
        var ex = act.Should().Throw<QueryBudgetExceededException>().Which;

        ex.Violations.Should().HaveCount(3);
        ex.Violations[0].Should().Be("queries: 3 > 2 allowed");
        ex.Violations[1].Should().MatchRegex(@"^duration: [\d.]+ ms > 0 ms allowed$");
        ex.Violations[2].Should().StartWith("diagnostics: 2 at or above Warning (QS004, QS008)");
        ex.Message.Should().StartWith("QueryShape budget exceeded: QueryBudgetTests.Budget_violations_are_listed_with_queries\n  - queries: 3 > 2 allowed\n");
        ex.Message.Should().Contain("\nQueries in this scope:\n  x2  ");
        ex.Message.Should().Contain("        at QueryBudgetTests.cs:");
        ex.Message.Should().Contain("  QS008 WARNING  Identical query executed 2 times: Product");
    }

    [Fact]
    public async Task Within_budget_passes_and_diagnostics_can_be_ignored()
    {
        using var scope = QueryShapeScope.Begin("ok", _shop.Options);
        await using var ctx = _shop.CreateContext();
        await ctx.Products.ToListAsync();

        scope.AssertBudget(new QueryBudget { MaxQueries = 1, FailOn = null });
        scope.AssertBudget(new QueryBudget { MaxQueries = 1, FailOn = Severity.Error });
        var act = () => scope.AssertBudget(new QueryBudget { FailOn = Severity.Warning });
        act.Should().Throw<QueryBudgetExceededException>().Which.Violations.Should().ContainSingle().Which.Should().StartWith("diagnostics: 1 at or above Warning (QS004)");
    }

    [Fact]
    public async Task Xunit_attribute_fails_the_test_after_it_ran()
    {
        var attribute = new QueryBudgetAttribute { MaxQueries = 1 };
        attribute.ToBudget().Should().BeEquivalentTo(new QueryBudget { MaxQueries = 1, MaxDurationMs = null, FailOn = Severity.Warning });
        var method = typeof(QueryBudgetTests).GetMethod(nameof(Xunit_attribute_fails_the_test_after_it_ran))!;

        attribute.Before(method);
        QueryShapeScope.Current.Should().NotBeNull();
        QueryShapeScope.Current!.Name.Should().Be("QueryBudgetTests.Xunit_attribute_fails_the_test_after_it_ran");
        await using (var ctx = _shop.CreateContext())
        {
            await ctx.Products.CountAsync();
            await ctx.Customers.CountAsync();
        }

        var act = () => attribute.After(method);
        act.Should().Throw<QueryBudgetExceededException>().Which.Violations[0].Should().Be("queries: 2 > 1 allowed");
        QueryShapeScope.Current.Should().BeNull("the attribute disposed its scope");
    }

    [Fact]
    public void Xunit_attribute_defaults_are_unlimited_except_diagnostics()
    {
        new QueryBudgetAttribute().ToBudget().Should().BeEquivalentTo(new QueryBudget { MaxQueries = null, MaxDurationMs = null, FailOn = Severity.Warning });
        new QueryBudgetAttribute { IgnoreDiagnostics = true }.ToBudget().FailOn.Should().BeNull();
        new QueryBudgetAttribute { FailOn = Severity.Error }.ToBudget().FailOn.Should().Be(Severity.Error);
    }
}
