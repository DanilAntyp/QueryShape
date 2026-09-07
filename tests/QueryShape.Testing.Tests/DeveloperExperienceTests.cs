using Microsoft.EntityFrameworkCore;
using QueryShape.Core.Tests.TestModel;
using QueryShape.Testing;
using QueryShape.Testing.Xunit;

namespace QueryShape.Testing.Tests;

/// <summary>The exact DX from CLAUDE.md section 6.1, run for real. The snapshot file next to this test is committed.</summary>
public class DeveloperExperienceTests : IDisposable
{
    private readonly SqliteShop _shop = new();

    public void Dispose() => _shop.Dispose();

    [Fact]
    public async Task Products_query_shape()
    {
        using var scope = QueryShapeScope.Begin();
        await using var ctx = _shop.CreateContext(QueryShapeTest.DefaultOptions);
        await ctx.Products.OrderBy(p => p.Sku).ToListAsync();
        await ctx.Customers.CountAsync();

        var result = await scope.MatchSnapshotAsync(options: new SnapshotOptions { CiMode = false, UpdateSnapshots = false });

        result.Outcome.Should().Be(SnapshotOutcome.Matched, "the snapshot file is committed next to this test");
        result.Path.Should().EndWith(Path.Combine("__querysnapshots__", "DeveloperExperienceTests.Products_query_shape.json"));
        result.Snapshot.QueryCount.Should().Be(2);
        result.Snapshot.Diagnostics.Select(d => d.RuleId).Should().Equal("QS004", "QS005");
    }

    [Fact]
    public async Task QueryShapeTest_Begin_variant()
    {
        using var test = QueryShapeTest.Begin();
        await using var ctx = _shop.CreateContext(QueryShapeTest.DefaultOptions);
        await ctx.Products.OrderBy(p => p.Sku).ToListAsync();

        test.TestName.Should().Be("DeveloperExperienceTests.QueryShapeTest_Begin_variant");
        test.SnapshotPath.Should().EndWith("DeveloperExperienceTests.QueryShapeTest_Begin_variant.json");
        test.Scope.Name.Should().Be("QueryShapeTest_Begin_variant");
        test.Scope.Commands.Should().ContainSingle().Which.CallSite.Should().NotBeNull();
        test.AssertBudget(new QueryBudget { MaxQueries = 1, FailOn = Severity.Error });
    }

    [Fact, QueryBudget(MaxQueries = 2, MaxDurationMs = 5000, FailOn = Severity.Error)]
    public async Task Stays_within_budget()
    {
        QueryShapeScope.Current.Should().NotBeNull("the attribute opened a scope before the test body");
        await using var ctx = _shop.CreateContext();
        await ctx.Customers.Where(c => c.Id == 1).ToListAsync();
        await ctx.Products.CountAsync();
    }
}
