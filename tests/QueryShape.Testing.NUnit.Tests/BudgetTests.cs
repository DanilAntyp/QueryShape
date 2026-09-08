using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using NUnit.Framework.Internal;
using QueryShape.Testing.NUnit;

namespace QueryShape.Testing.NUnit.Tests;

[TestFixture]
public class BudgetTests
{
    private MiniShop _shop = null!;

    [SetUp]
    public void SetUp() => _shop = new MiniShop();

    [TearDown]
    public void TearDown() => _shop.Dispose();

    [Test, QueryBudget(MaxQueries = 2, FailOn = Severity.Error)]
    public async Task Attribute_opens_a_scope_the_test_body_runs_in()
    {
        Assert.That(QueryShapeScope.Current, Is.Not.Null, "BeforeTest opened the scope in the test's execution context");
        using var ctx = _shop.Create();
        await ctx.Products.CountAsync();
        Assert.That(QueryShapeScope.Current!.CommandCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Exceeding_the_budget_fails_after_the_test()
    {
        var attribute = new QueryBudgetAttribute { MaxQueries = 1 };
        var test = new TestMethod(new MethodWrapper(typeof(BudgetTests), nameof(Exceeding_the_budget_fails_after_the_test)));

        attribute.BeforeTest(test);
        using (var ctx = _shop.Create())
        {
            await ctx.Products.CountAsync();
            await ctx.Products.CountAsync();
        }

        var ex = Assert.Throws<QueryBudgetExceededException>(() => attribute.AfterTest(test));
        Assert.That(ex!.Violations[0], Is.EqualTo("queries: 2 > 1 allowed"));
        Assert.That(QueryShapeScope.Current, Is.Null, "the attribute disposed its scope");
    }
}
