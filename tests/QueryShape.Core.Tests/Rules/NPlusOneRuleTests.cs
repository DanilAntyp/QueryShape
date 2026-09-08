using QueryShape.Rules;

namespace QueryShape.Core.Tests.Rules;

public class NPlusOneRuleTests
{
    private static readonly QueryInfo s_orders = Synthetic.Query(
        "DbSet<Order>()\n    .Where(o => o.CustomerId == @__id_0)", "Order", hasFilter: true,
        keyFilters: [new KeyFilter("Order", "CustomerId", false, "Customer", "Orders", "Customer")]);

    private static readonly QueryInfo s_customers = Synthetic.Query("DbSet<Customer>()", "Customer");

    [Fact]
    public void Fires_when_same_shape_runs_at_threshold_with_varying_parameters()
    {
        using var scope = Synthetic.Scope(o => o.NPlusOneThreshold = 5);
        scope.Add("SELECT * FROM Customers", query: s_customers, callSite: new CallSite("/src/OrderService.cs", 40, "OrderService.GetAll"));
        for (var i = 0; i < 5; i++)
        {
            scope.Add("SELECT * FROM Orders WHERE CustomerId = @p", parameterHash: $"p{i}", query: s_orders, rows: 3, callSite: new CallSite("/src/OrderService.cs", 42, "OrderService.GetAll"));
        }

        var d = new NPlusOneRule().Analyze(scope).Should().ContainSingle().Subject;
        d.RuleId.Should().Be("QS001");
        d.Severity.Should().Be(Severity.Error);
        d.Title.Should().Be("N+1 query: Order by CustomerId executed 5 times at OrderService.cs:42 OrderService.GetAll");
        d.Explanation.Should().Contain("cannot see the loop").And.Contain("5 times").And.Contain("Order.CustomerId");
        d.Evidence.Count.Should().Be(5);
        d.Evidence.Rows.Should().Be(15);
        d.Evidence.Details!["distinctParameterSets"].Should().Be("5");
        d.SuggestedFix.Should().NotBeNull();
        d.SuggestedFix!.Summary.Should().Be("Add .Include(c => c.Orders) to the Customer query at OrderService.cs:40 OrderService.GetAll, then read c.Orders in the loop instead of querying");
        d.SuggestedFix.Kind.Should().Be(FixKind.CodeChange);
        d.SuggestedFix.BeforeSnippet.Should().Be("DbSet<Customer>()");
        d.SuggestedFix.AfterSnippet.Should().Be("DbSet<Customer>()\n    .Include(c => c.Orders)");
        d.SuggestedFix.UnifiedDiff.Should().BeNull("the source file does not exist");
        d.SuggestedFix.DocsUrl.Should().EndWith("/QS001.md");
    }

    [Fact]
    public void Does_not_fire_below_threshold()
    {
        using var scope = Synthetic.Scope(o => o.NPlusOneThreshold = 5);
        for (var i = 0; i < 4; i++)
        {
            scope.Add("SELECT * FROM Orders WHERE CustomerId = @p", parameterHash: $"p{i}", query: s_orders);
        }

        new NPlusOneRule().Analyze(scope).Should().BeEmpty();
    }

    [Fact]
    public void Does_not_fire_when_parameters_are_identical()
    {
        using var scope = Synthetic.Scope(o => o.NPlusOneThreshold = 3);
        for (var i = 0; i < 6; i++)
        {
            scope.Add("SELECT * FROM Orders WHERE CustomerId = @p", parameterHash: "same", query: s_orders);
        }

        new NPlusOneRule().Analyze(scope).Should().BeEmpty("identical parameters are QS008's business");
    }

    [Fact]
    public void Raw_sql_loops_are_detected_with_a_generic_fix()
    {
        using var scope = Synthetic.Scope(o => o.NPlusOneThreshold = 3);
        for (var i = 0; i < 3; i++)
        {
            scope.Add("SELECT * FROM Orders WHERE CustomerId = @p", parameterHash: $"p{i}", source: QuerySource.Raw);
        }

        var d = new NPlusOneRule().Analyze(scope).Should().ContainSingle().Subject;
        d.Title.Should().StartWith("N+1 query: SELECT * FROM Orders WHERE CustomerId = @p0 executed 3 times");
        d.Explanation.Should().Contain("raw SQL");
        d.SuggestedFix!.BeforeSnippet.Should().BeNull();
    }

    [Fact]
    public void Ignores_save_changes_and_failed_commands()
    {
        using var scope = Synthetic.Scope(o => o.NPlusOneThreshold = 2);
        for (var i = 0; i < 4; i++)
        {
            scope.Add("INSERT INTO Orders VALUES (@p)", parameterHash: $"p{i}", source: QuerySource.SaveChanges);
        }

        new NPlusOneRule().Analyze(scope).Should().BeEmpty();
    }

    [Fact]
    public void Produces_a_two_hunk_patch_that_includes_and_reads_the_navigation()
    {
        var file = Path.Combine(Path.GetTempPath(), $"qs-{Guid.NewGuid():N}.cs");
        File.WriteAllLines(file,
        [
            "public async Task<List<Customer>> GetAll()",
            "{",
            "    var customers = await _db.Customers.ToListAsync();",
            "    foreach (var c in customers)",
            "    {",
            "        var orders = await _db.Orders.Where(o => o.CustomerId == c.Id).ToListAsync();",
            "        c.Orders = orders;",
            "    }",
            "    return customers;",
            "}",
        ]);
        try
        {
            using var scope = Synthetic.Scope(o => o.NPlusOneThreshold = 2);
            scope.Add("SELECT * FROM Customers", query: s_customers, callSite: new CallSite(file, 3, "OrderService.GetAll"));
            scope.Add("SELECT * FROM Orders WHERE CustomerId = @p", parameterHash: "a", query: s_orders, callSite: new CallSite(file, 6, "OrderService.GetAll"));
            scope.Add("SELECT * FROM Orders WHERE CustomerId = @p", parameterHash: "b", query: s_orders, callSite: new CallSite(file, 6, "OrderService.GetAll"));

            var fix = new NPlusOneRule().Analyze(scope).Single().SuggestedFix!;
            fix.IsPartial.Should().BeFalse();
            fix.ManualStep.Should().BeNull();
            fix.UnifiedDiff.Should().Be(
                "--- a/" + file.TrimStart('/') + "\n" +
                "+++ b/" + file.TrimStart('/') + "\n" +
                "@@ -1,9 +1,9 @@\n" +
                " public async Task<List<Customer>> GetAll()\n" +
                " {\n" +
                "-    var customers = await _db.Customers.ToListAsync();\n" +
                "+    var customers = await _db.Customers.Include(c => c.Orders).ToListAsync();\n" +
                "     foreach (var c in customers)\n" +
                "     {\n" +
                "-        var orders = await _db.Orders.Where(o => o.CustomerId == c.Id).ToListAsync();\n" +
                "+        var orders = c.Orders;\n" +
                "         c.Orders = orders;\n" +
                "     }\n" +
                "     return customers;\n");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Assigning_the_navigation_itself_deletes_the_loop_query_line()
    {
        var file = Path.Combine(Path.GetTempPath(), $"qs-{Guid.NewGuid():N}.cs");
        File.WriteAllLines(file,
        [
            "var customers = await _db.Customers.ToListAsync();",
            "foreach (var customer in customers)",
            "{",
            "    customer.Orders = await _db.Orders.Where(o => o.CustomerId == customer.Id).ToListAsync();",
            "}",
        ]);
        try
        {
            using var scope = Synthetic.Scope(o => o.NPlusOneThreshold = 2);
            scope.Add("SELECT * FROM Customers", query: s_customers, callSite: new CallSite(file, 1, "X.M"));
            scope.Add("SELECT * FROM Orders WHERE CustomerId = @p", parameterHash: "a", query: s_orders, callSite: new CallSite(file, 4, "X.M"));
            scope.Add("SELECT * FROM Orders WHERE CustomerId = @p", parameterHash: "b", query: s_orders, callSite: new CallSite(file, 4, "X.M"));

            var fix = new NPlusOneRule().Analyze(scope).Single().SuggestedFix!;
            fix.IsPartial.Should().BeFalse();
            fix.UnifiedDiff.Should().Contain("@@ -1,5 +1,4 @@\n")
                .And.Contain("-    customer.Orders = await _db.Orders.Where(o => o.CustomerId == customer.Id).ToListAsync();\n }")
                .And.NotContain("+    customer.Orders");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Principal_lookup_by_key_rewrites_to_the_reference_navigation()
    {
        var file = Path.Combine(Path.GetTempPath(), $"qs-{Guid.NewGuid():N}.cs");
        File.WriteAllLines(file,
        [
            "var orders = await _db.Orders.Take(20).ToListAsync();",
            "foreach (var order in orders)",
            "{",
            "    var customer = await _db.Customers.SingleAsync(c => c.Id == order.CustomerId);",
            "    names.Add(customer.Name);",
            "}",
        ]);
        try
        {
            var byKey = Synthetic.Query("DbSet<Customer>()\n    .Single(c => c.Id == @__order_CustomerId_0)", "Customer", hasFilter: true, hasLimit: true,
                keyFilters: [new KeyFilter("Customer", "Id", true, "Order", "Customer", "Orders")]);
            var ordersQuery = Synthetic.Query("DbSet<Order>()\n    .Take(@__p_0)", "Order", hasLimit: true);
            using var scope = Synthetic.Scope(o => o.NPlusOneThreshold = 2);
            scope.Add("SELECT * FROM Orders LIMIT @p", query: ordersQuery, callSite: new CallSite(file, 1, "X.M"));
            scope.Add("SELECT * FROM Customers WHERE Id = @p", parameterHash: "a", query: byKey, callSite: new CallSite(file, 4, "X.M"));
            scope.Add("SELECT * FROM Customers WHERE Id = @p", parameterHash: "b", query: byKey, callSite: new CallSite(file, 4, "X.M"));

            var fix = new NPlusOneRule().Analyze(scope).Single().SuggestedFix!;
            fix.Summary.Should().StartWith("Add .Include(o => o.Customer) to the Order query");
            fix.IsPartial.Should().BeFalse();
            fix.UnifiedDiff.Should().Contain("+var orders = await _db.Orders.Take(20).Include(o => o.Customer).ToListAsync();", "the Include goes right before the terminal operator")
                .And.Contain("+    var customer = order.Customer;");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Unrecognized_loop_shape_yields_a_partial_fix_with_a_manual_step()
    {
        var file = Path.Combine(Path.GetTempPath(), $"qs-{Guid.NewGuid():N}.cs");
        File.WriteAllLines(file,
        [
            "var customers = await _db.Customers.ToListAsync();",
            "foreach (var c in customers)",
            "{",
            "    List<Order> orders = await _db.Orders.Where(o => o.CustomerId == c.Id).ToListAsync();   // explicitly typed",
            "}",
        ]);
        try
        {
            using var scope = Synthetic.Scope(o => o.NPlusOneThreshold = 2);
            scope.Add("SELECT * FROM Customers", query: s_customers, callSite: new CallSite(file, 1, "X.M"));
            scope.Add("SELECT * FROM Orders WHERE CustomerId = @p", parameterHash: "a", query: s_orders, callSite: new CallSite(file, 4, "X.M"));
            scope.Add("SELECT * FROM Orders WHERE CustomerId = @p", parameterHash: "b", query: s_orders, callSite: new CallSite(file, 4, "X.M"));

            var fix = new NPlusOneRule().Analyze(scope).Single().SuggestedFix!;
            fix.IsPartial.Should().BeTrue();
            fix.UnifiedDiff.Should().Contain("+var customers = await _db.Customers.Include(c => c.Orders).ToListAsync();").And.NotContain("+    var orders");
            fix.ManualStep.Should().Be("Inside the loop, replace the Order query with a read of c.Orders (the Include now fills it); the patch only adds the Include.");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Produces_a_unified_diff_when_the_parent_call_site_file_is_readable()
    {
        var file = Path.Combine(Path.GetTempPath(), $"qs-{Guid.NewGuid():N}.cs");
        File.WriteAllLines(file,
        [
            "public async Task<List<Customer>> GetAll()",
            "{",
            "    var customers = await _db.Customers.ToListAsync();",
            "    foreach (var c in customers) c.Orders = await _db.Orders.Where(o => o.CustomerId == c.Id).ToListAsync();",
            "    return customers;",
            "}",
        ]);
        try
        {
            using var scope = Synthetic.Scope(o => o.NPlusOneThreshold = 2);
            scope.Add("SELECT * FROM Customers", query: s_customers, callSite: new CallSite(file, 3, "OrderService.GetAll"));
            scope.Add("SELECT * FROM Orders WHERE CustomerId = @p", parameterHash: "a", query: s_orders, callSite: new CallSite(file, 4, "OrderService.GetAll"));
            scope.Add("SELECT * FROM Orders WHERE CustomerId = @p", parameterHash: "b", query: s_orders, callSite: new CallSite(file, 4, "OrderService.GetAll"));

            var fix = new NPlusOneRule().Analyze(scope).Single().SuggestedFix!;
            fix.UnifiedDiff.Should().NotBeNull();
            fix.UnifiedDiff.Should().Contain("-    var customers = await _db.Customers.ToListAsync();")
                .And.Contain("+    var customers = await _db.Customers.Include(c => c.Orders).ToListAsync();")
                .And.StartWith("--- a/");
            fix.IsPartial.Should().BeTrue("the loop body sits on the foreach line, which is not a shape we rewrite");
            fix.ManualStep.Should().Contain("replace the Order query with a read of c.Orders");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
