using Microsoft.EntityFrameworkCore;
using QueryShape.Core.Tests.TestModel;
using QueryShape.Reporting;

namespace QueryShape.Core.Tests.Rules;

/// <summary>Real EF Core + SQLite: each pathology written the way a developer would write it.</summary>
public class RuleIntegrationTests : IDisposable
{
    private readonly SqliteShop _shop = new(customers: 10, ordersPerCustomer: 3);

    public void Dispose() => _shop.Dispose();

    [Fact]
    public async Task N_plus_one_over_customers_orders_is_detected_with_include_fix()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        var customers = await ctx.Customers.ToListAsync();
        foreach (var customer in customers)
        {
            customer.Orders = await ctx.Orders.Where(o => o.CustomerId == customer.Id).ToListAsync();
        }

        var diagnoses = scope.Analyze();
        var n1 = diagnoses.Should().ContainSingle(d => d.RuleId == "QS001").Subject;
        n1.Title.Should().StartWith("N+1 query: Order by CustomerId executed 10 times at RuleIntegrationTests.cs:");
        n1.CallSite!.Member.Should().Be("RuleIntegrationTests.N_plus_one_over_customers_orders_is_detected_with_include_fix");
        n1.SuggestedFix!.Summary.Should().StartWith("Add .Include(c => c.Orders) to the Customer query at RuleIntegrationTests.cs:");
        n1.SuggestedFix.BeforeSnippet.Should().Be("DbSet<Customer>()");
        n1.SuggestedFix.AfterSnippet.Should().Be("DbSet<Customer>()\n    .Include(c => c.Orders)");
        n1.SuggestedFix.UnifiedDiff.Should().Contain("+        var customers = await ctx.Customers.Include(c => c.Orders).ToListAsync();");
        n1.Evidence.Rows.Should().Be(30);

        // The unfiltered customers query is also unbounded (known, expected).
        diagnoses.Should().Contain(d => d.RuleId == "QS004" && d.Title.StartsWith("Unbounded query: loads every Customer row (10 rows)"));
        diagnoses.Should().NotContain(d => d.RuleId == "QS008");
    }

    [Fact]
    public async Task N_plus_one_loading_principal_by_key_suggests_reference_include()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        var orders = await ctx.Orders.Take(20).ToListAsync();
        foreach (var order in orders)
        {
            order.Customer = await ctx.Customers.SingleAsync(c => c.Id == order.CustomerId);
        }

        var n1 = scope.Analyze().Should().ContainSingle(d => d.RuleId == "QS001").Subject;
        n1.Title.Should().StartWith("N+1 query: Customer by Id executed 20 times");
        n1.SuggestedFix!.Summary.Should().StartWith("Add .Include(o => o.Customer) to the Order query");
    }

    [Fact]
    public async Task Duplicate_identical_query_is_detected()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        var country = "DE";
        _ = await ctx.Customers.Where(c => c.Country == country).CountAsync();
        _ = await ctx.Customers.Where(c => c.Country == country).CountAsync();

        var d = scope.Analyze().Should().ContainSingle(d => d.RuleId == "QS008").Subject;
        d.Title.Should().StartWith("Identical query executed 2 times: Customer");
    }

    [Fact]
    public async Task Unbounded_query_is_detected_with_row_count()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        await ctx.Orders.Include(o => o.Lines).ToListAsync();

        var d = scope.Analyze().Should().ContainSingle(d => d.RuleId == "QS004").Subject;
        d.Title.Should().StartWith("Unbounded query: loads every Order row (60 rows)");
        d.Explanation.Should().Contain("The Include of Lines multiplies the rows transferred.");
        d.SuggestedFix!.Summary.Should().Be("Filter the Order query (.Where) or page it (.OrderBy(o => o.Id).Take(n))");
    }

    [Fact]
    public async Task Bounded_queries_produce_no_diagnoses()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        await ctx.Orders.Where(o => o.Total > 100).OrderBy(o => o.Id).Take(5).ToListAsync();
        await ctx.Customers.FirstOrDefaultAsync(c => c.Id == 3);
        await ctx.Products.AnyAsync();
        await ctx.OrderLines.Where(l => l.OrderId == 1).SumAsync(l => l.Quantity);

        scope.Analyze().Should().BeEmpty();
    }

    [Fact]
    public async Task Client_evaluation_in_projection_is_detected()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        var rows = await ctx.Orders.Where(o => o.Total > 0).Select(o => new { o.Id, Label = FormatTotal(o.Total) }).ToListAsync();
        rows.Should().HaveCount(30);

        var d = scope.Analyze().Should().ContainSingle(d => d.RuleId == "QS003").Subject;
        d.Title.Should().StartWith("Client-side evaluation: RuleIntegrationTests.FormatTotal(Decimal) runs in memory for every Order row");
        d.Explanation.Should().Contain("30 rows");
        d.Evidence.Details!["operator"].Should().Be("Select");
    }

    [Fact]
    public async Task Translatable_calls_are_not_flagged_as_client_evaluation()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        await ctx.Customers.Where(c => c.Name.StartsWith("Cust") && EF.Functions.Like(c.Country, "D%"))
            .Select(c => new { Upper = c.Name.ToUpper(), Len = c.Name.Length, Year = c.Orders.Max(o => o.PlacedAt.Year) })
            .ToListAsync();

        scope.Analyze().Should().NotContain(d => d.RuleId == "QS003");
    }

    [Fact]
    public async Task Formatted_output_is_readable()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();
        var customers = await ctx.Customers.ToListAsync();
        foreach (var customer in customers)
        {
            await ctx.Orders.Where(o => o.CustomerId == customer.Id).ToListAsync();
        }

        var text = DiagnosisFormatter.Format(scope.Analyze());
        text.Should().StartWith("QS001 ERROR  N+1 query: Order by CustomerId executed 10 times at RuleIntegrationTests.cs:");
        text.Should().Contain("\n  why  EF Core translates each LINQ query into exactly one SQL statement");
        text.Should().Contain("\n  fix  Add .Include(c => c.Orders) to the Customer query at RuleIntegrationTests.cs:");
        text.Should().Contain("       before: DbSet<Customer>()\n");
        text.Should().Contain("       after:  DbSet<Customer>()\n                   .Include(c => c.Orders)\n");
        text.Should().Contain("       patch:\n");
        text.Should().Contain("\n  data 10 queries, ");
        text.Should().Contain("\n  sql  SELECT \"t0\".\"Id\", \"t0\".\"CustomerId\"").And.Contain("WHERE \"t0\".\"CustomerId\" = @p0\n");
        text.Should().Contain("\n  docs https://");
        text.Should().Contain("QS004 WARNING  Unbounded query: loads every Customer row (10 rows)");
    }

    private static string FormatTotal(decimal total) => total.ToString("C");
}
