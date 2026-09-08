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

        // The unfiltered customers query returned 10 rows: lookup-table-sized (below UnboundedMinimumRows), so QS004 is an Info, not a Warning.
        diagnoses.Should().Contain(d => d.RuleId == "QS004" && d.Severity == Severity.Info && d.Title.StartsWith("Unbounded query: loads every Customer row (10 rows)"));
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
    public async Task Unfiltered_lookup_table_is_info_and_group_by_aggregate_is_not_unbounded()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        await ctx.Products.AsNoTracking().ToListAsync();                                                       // 5 rows: a lookup table
        await ctx.Orders.GroupBy(o => o.CustomerId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(); // groups, not rows

        scope.Commands[1].Query!.HasGrouping.Should().BeTrue();
        var d = scope.Analyze().Should().ContainSingle(x => x.RuleId == "QS004").Subject;
        d.Severity.Should().Be(Severity.Info, "5 rows is a lookup table until the data says otherwise");
        d.Title.Should().StartWith("Unbounded query: loads every Product row (5 rows)");

        using var strict = QueryShapeScope.Begin(options: new QueryShapeOptions { UnboundedMinimumRows = 5 });
        await ctx.Products.AsNoTracking().ToListAsync();
        strict.Analyze().Should().ContainSingle(x => x.RuleId == "QS004").Which.Severity.Should().Be(Severity.Warning);
    }

    [Fact]
    public async Task Bounded_queries_produce_nothing_above_info()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        await ctx.Orders.Where(o => o.Total > 100).OrderBy(o => o.Id).Take(5).ToListAsync();
        await ctx.Customers.FirstOrDefaultAsync(c => c.Id == 3);
        await ctx.Products.AnyAsync();
        await ctx.OrderLines.Where(l => l.OrderId == 1).SumAsync(l => l.Quantity);

        var diagnoses = scope.Analyze();
        diagnoses.Should().OnlyContain(d => d.RuleId == "QS005" && d.Severity == Severity.Info, "tracked read-only entity queries are Info-level hints");
        diagnoses.Should().HaveCount(2, "the two entity-returning queries (Order, Customer); Any/Sum return scalars");
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
        text.Should().Contain("QS004 INFO  Unbounded query: loads every Customer row (10 rows)");
    }

    private static string FormatTotal(decimal total) => total.ToString("C");
}

public class RemainingRuleIntegrationTests : IDisposable
{
    private readonly SqliteShop _shop = new(customers: 12, ordersPerCustomer: 5, linesPerOrder: 4);

    public void Dispose() => _shop.Dispose();

    [Fact]
    public async Task Cartesian_explosion_and_split_query_are_detected_from_real_row_counts()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        // 12 customers x 5 orders x 4 lines = 240 rows for 12 roots -> QS002
        await ctx.Customers.Include(c => c.Orders).ThenInclude(o => o.Lines).Include(c => c.Orders).ToListAsync();

        var cmd = scope.Commands.Single();
        cmd.RowsReturned.Should().Be(240);
        cmd.DistinctRootsEstimate.Should().Be(12);
        var diagnoses = scope.Analyze();
        diagnoses.Should().Contain(d => d.RuleId == "QS002" && d.Title.StartsWith("Cartesian explosion: 240 rows for 12 Customer entities"));
        diagnoses.Should().NotContain(d => d.RuleId == "QS006");

        // Split query: two commands, no multiplication.
        using var split = QueryShapeScope.Begin(options: _shop.Options);
        await ctx.Customers.Include(c => c.Orders).ThenInclude(o => o.Lines).AsSplitQuery().ToListAsync();
        split.Analyze().Should().NotContain(d => d.RuleId == "QS002" || d.RuleId == "QS006");
    }

    [Fact]
    public async Task Tracking_on_read_only_query_is_info_and_disappears_after_save_changes()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        var products = await ctx.Products.Where(p => p.Price > 10).ToListAsync();
        scope.Analyze().Should().ContainSingle(d => d.RuleId == "QS005").Which.Title.Should().StartWith("Tracked read-only query: 4 Product entities loaded with change tracking");

        products[0].Price += 1;
        await ctx.SaveChangesAsync();
        scope.Analyze().Should().NotContain(d => d.RuleId == "QS005");
    }

    [Fact]
    public async Task Keyless_entity_query_is_never_reported_as_tracked()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        await ctx.Set<CustomerSummary>().ToListAsync();

        var cmd = scope.Commands.Single();
        cmd.Query!.ReturnsEntities.Should().BeTrue();
        cmd.Query.IsTracking.Should().BeFalse("keyless entity types are never tracked by EF Core");
        cmd.IsTracking.Should().BeFalse();
        scope.Analyze().Should().NotContain(d => d.RuleId == "QS005");
    }

    [Fact]
    public async Task Modifying_an_included_entity_counts_as_using_the_root_query_tracking()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        var customers = await ctx.Customers.Include(c => c.Orders).ToListAsync();
        scope.Commands.Single().Query!.IncludedEntityTypes.Should().Equal(typeof(Order).FullName!);

        customers[0].Orders[0].Total += 1;
        await ctx.SaveChangesAsync();

        scope.Analyze().Should().NotContain(d => d.RuleId == "QS005", "the Orders loaded through the Include were modified and saved");
    }

    [Fact]
    public async Task Contains_on_large_collection_is_detected_from_the_json_parameter()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        var ids = Enumerable.Range(1, 600).ToList();
        await ctx.OrderLines.Where(l => ids.Contains(l.Id)).ToListAsync();

        scope.Commands.Single().MaxCollectionParameterCount.Should().Be(600);
        scope.Commands.Single().Query!.HasParameterCollectionContains.Should().BeTrue();
        scope.Analyze().Should().ContainSingle(d => d.RuleId == "QS007").Which.Title.Should().StartWith("Contains over 600 values on the OrderLine query (threshold 500)");
    }

    [Fact]
    public async Task Query_in_loop_is_detected_by_call_site()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        var orders = await ctx.Orders.OrderBy(o => o.Id).Take(6).ToListAsync();
        foreach (var order in orders)
        {
            await SummarizeAsync(ctx, order);
        }

        var d = scope.Analyze().Should().ContainSingle(d => d.RuleId == "QS009").Subject;
        d.Title.Should().Be("Queries in a loop: RemainingRuleIntegrationTests.SummarizeAsync issued 12 queries of 2 shapes (Customer, OrderLine) in one scope");
    }

    [Fact]
    public async Task Raw_sql_concatenation_is_detected()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        foreach (var name in new[] { "Customer 1", "Customer 2", "Customer 3" })
        {
#pragma warning disable EF1002
            await ctx.Customers.FromSqlRaw($"SELECT * FROM Customers WHERE Name = '{name}'").ToListAsync();
#pragma warning restore EF1002
        }

        var d = scope.Analyze().Should().ContainSingle(d => d.RuleId == "QS010").Subject;
        d.Title.Should().StartWith("Raw SQL built from values: 3 text variants of \"SELECT * FROM Customers WHERE Name = ?\"");
        d.CallSite!.Member.Should().Be("RemainingRuleIntegrationTests.Raw_sql_concatenation_is_detected");
    }

    [Fact]
    public async Task Paging_without_order_by_is_detected_from_ef_core_warning()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        await ctx.Orders.AsNoTracking().Skip(5).Take(5).ToListAsync();
        await ctx.Orders.AsNoTracking().OrderBy(o => o.PlacedAt).ThenBy(o => o.Id).Skip(5).Take(5).ToListAsync();

        var diagnoses = scope.Analyze();
        diagnoses.Should().ContainSingle(d => d.RuleId == "QS011").Which.Title.Should().StartWith("Non-deterministic paging: Skip/Take without OrderBy on Order");
        diagnoses.Should().NotContain(d => d.RuleId == "QS005");
    }

    private static async Task<(string, int)> SummarizeAsync(ShopContext ctx, Order order)
    {
        var customer = await ctx.Customers.FirstAsync(c => c.Id == order.CustomerId);
        var lines = await ctx.OrderLines.CountAsync(l => l.OrderId == order.Id);
        return (customer.Name, lines);
    }
}
