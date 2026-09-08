using Microsoft.EntityFrameworkCore.Diagnostics;
using QueryShape.Normalization;
using QueryShape.Rules;

namespace QueryShape.Core.Tests.Rules;

public class CartesianExplosionRuleTests
{
    private static CapturedCommand IncludeQuery(QueryShapeScope scope, int rows, int roots, string splitting = "SingleQuery")
    {
        var q = new QueryInfo
        {
            Expression = "DbSet<Customer>()\n    .Include(c => c.Orders)\n    .Include(c => c.Addresses)",
            ExpressionHash = Fingerprint.Compute("inc" + splitting),
            RootEntityShortName = "Customer",
            RootEntityType = "Test.Customer",
            RootTableName = "Customers",
            ReturnsEntities = true,
            IsTracking = true,
            CollectionIncludes = ["Orders", "Addresses"],
            SplittingBehavior = splitting,
        };
        var c = scope.Add("SELECT * FROM Customers c LEFT JOIN Orders o ON ... LEFT JOIN Addresses a ON ...", query: q, rows: rows);
        c.DistinctRootsEstimate = roots;
        return c;
    }

    [Fact]
    public void QS002_fires_when_rows_are_a_large_multiple_of_roots()
    {
        using var scope = Synthetic.Scope();
        IncludeQuery(scope, rows: 400, roots: 40);

        var d = new CartesianExplosionRule().Analyze(scope).Should().ContainSingle().Subject;
        d.RuleId.Should().Be("QS002");
        d.Severity.Should().Be(Severity.Error);
        d.Title.Should().Be("Cartesian explosion: 400 rows for 40 Customer entities (Orders, Addresses)");
        d.Explanation.Should().Contain("LEFT JOIN").And.Contain("10 per root");
        d.SuggestedFix!.Summary.Should().Be("Add .AsSplitQuery() to the Customer query so each collection loads with its own SELECT");
        d.SuggestedFix.AfterSnippet.Should().Be("DbSet<Customer>()\n    .AsSplitQuery()\n    .Include(c => c.Orders)\n    .Include(c => c.Addresses)");
        new MissingSplitQueryRule().Analyze(scope).Should().BeEmpty("QS002 owns the explosive case");
    }

    [Fact]
    public void QS006_fires_for_mild_multiplication_and_QS002_stays_silent()
    {
        using var scope = Synthetic.Scope();
        IncludeQuery(scope, rows: 60, roots: 20);

        new CartesianExplosionRule().Analyze(scope).Should().BeEmpty();
        var d = new MissingSplitQueryRule().Analyze(scope).Should().ContainSingle().Subject;
        d.RuleId.Should().Be("QS006");
        d.Severity.Should().Be(Severity.Warning);
        d.Title.Should().Be("Split query candidate: 2 collection includes (Orders, Addresses) in one query, 60 rows for 20 Customer entities");
        d.SuggestedFix!.Summary.Should().StartWith("Add .AsSplitQuery() to the Customer query");
    }

    [Fact]
    public void Neither_fires_for_split_queries_or_tiny_results()
    {
        using var scope = Synthetic.Scope();
        IncludeQuery(scope, rows: 400, roots: 40, splitting: "SplitQuery");
        new CartesianExplosionRule().Analyze(scope).Should().BeEmpty();
        new MissingSplitQueryRule().Analyze(scope).Should().BeEmpty();

        using var small = Synthetic.Scope();
        IncludeQuery(small, rows: 20, roots: 20);
        new CartesianExplosionRule().Analyze(small).Should().BeEmpty();
        new MissingSplitQueryRule().Analyze(small).Should().BeEmpty("no multiplication at all");
    }
}

public class TrackingOnReadOnlyQueryRuleTests
{
    [Fact]
    public void Fires_for_tracked_entity_query_without_save_changes()
    {
        using var scope = Synthetic.Scope();
        var q = Synthetic.Query("DbSet<Product>()\n    .Where(p => p.Price > @__min_0)", "Product", hasFilter: true, tracking: true);
        scope.Add("SELECT * FROM Products WHERE Price > @p", query: q, rows: 10, callSite: new CallSite("/a/Catalog.cs", 9, "Catalog.List"));

        var d = new TrackingOnReadOnlyQueryRule().Analyze(scope).Should().ContainSingle().Subject;
        d.RuleId.Should().Be("QS005");
        d.Severity.Should().Be(Severity.Info);
        d.Title.Should().Be("Tracked read-only query: 10 Product entities loaded with change tracking but never modified at Catalog.cs:9 Catalog.List");
        d.Explanation.Should().Contain("snapshot of every property");
        d.SuggestedFix!.Summary.Should().StartWith("Add .AsNoTracking() to the Product query");
        d.SuggestedFix.AfterSnippet.Should().Be("DbSet<Product>()\n    .AsNoTracking()\n    .Where(p => p.Price > @__min_0)");
    }

    [Fact]
    public void Silent_when_the_entity_type_was_saved_or_query_is_untracked_or_a_projection()
    {
        using var scope = Synthetic.Scope();
        scope.Add("SELECT * FROM Products", query: Synthetic.Query("a", "Product", tracking: true), rows: 3);
        scope.Record(new SaveChangesRecord(Guid.NewGuid(), ["Test.Product"], 1));
        scope.Add("SELECT * FROM Orders", query: Synthetic.Query("b", "Order", tracking: false), rows: 3);
        scope.Add("SELECT Name FROM Customers", query: new QueryInfo { Expression = "c", ExpressionHash = "c", RootEntityShortName = "Customer", ReturnsEntities = false, IsTracking = false }, rows: 3);

        new TrackingOnReadOnlyQueryRule().Analyze(scope).Should().BeEmpty();
    }
}

public class ContainsLargeCollectionRuleTests
{
    [Fact]
    public void Fires_above_threshold_with_provider_specific_mechanism()
    {
        using var scope = Synthetic.Scope(o => o.ContainsCollectionThreshold = 500);
        var q = Synthetic.Query("DbSet<OrderLine>()\n    .Where(l => @__ids_0.Contains(l.Id))", "OrderLine", hasFilter: true);
        var cmd = new CapturedCommand
        {
            CommandText = "SELECT * FROM OrderLines WHERE Id IN (SELECT value FROM json_each(@ids))",
            Shape = "SELECT * FROM OrderLines WHERE Id IN (SELECT value FROM json_each(@p0))",
            Fingerprint = "abcabcabcabc",
            Source = QuerySource.Linq,
            ExecuteMethod = DbCommandMethod.ExecuteReader,
            ParameterHash = "x",
            Query = q,
            ProviderName = "Microsoft.EntityFrameworkCore.Sqlite",
            MaxCollectionParameterCount = 600,
            CommandId = Guid.NewGuid(),
        };
        scope.Record(cmd);

        var d = new ContainsLargeCollectionRule().Analyze(scope).Should().ContainSingle().Subject;
        d.RuleId.Should().Be("QS007");
        d.Title.Should().Be("Contains over 600 values on the OrderLine query (threshold 500)");
        d.Explanation.Should().Contain("json_each").And.Contain("2 100 parameters");
        d.SuggestedFix!.Summary.Should().StartWith("Replace the in-memory list with a query the database can join");
    }

    [Fact]
    public void Silent_at_or_below_threshold()
    {
        using var scope = Synthetic.Scope(o => o.ContainsCollectionThreshold = 500);
        scope.Record(new CapturedCommand
        {
            CommandText = "x", Shape = "x", Fingerprint = "f", Source = QuerySource.Linq, ExecuteMethod = DbCommandMethod.ExecuteReader,
            ParameterHash = "p", MaxCollectionParameterCount = 500, CommandId = Guid.NewGuid(),
        });
        new ContainsLargeCollectionRule().Analyze(scope).Should().BeEmpty();
    }
}

public class QueryInLoopRuleTests
{
    [Fact]
    public void Fires_for_one_member_issuing_two_repeated_shapes()
    {
        using var scope = Synthetic.Scope(o => o.NPlusOneThreshold = 4);
        var site1 = new CallSite("/a/Summaries.cs", 12, "Summaries.ForOrderAsync");
        var site2 = new CallSite("/a/Summaries.cs", 13, "Summaries.ForOrderAsync");
        for (var i = 0; i < 3; i++)
        {
            scope.Add("SELECT * FROM Customers WHERE Id = @p", parameterHash: "c" + i, query: Synthetic.Query("cust", "Customer", hasFilter: true, hasLimit: true), callSite: site1);
            scope.Add("SELECT COUNT(*) FROM OrderLines WHERE OrderId = @p", parameterHash: "o" + i, query: Synthetic.Query("lines", "OrderLine", hasFilter: true, hasLimit: true), callSite: site2);
        }

        var d = new QueryInLoopRule().Analyze(scope).Should().ContainSingle().Subject;
        d.RuleId.Should().Be("QS009");
        d.Title.Should().Be("Queries in a loop: Summaries.ForOrderAsync issued 6 queries of 2 shapes (Customer, OrderLine) in one scope");
        d.Explanation.Should().Contain("(lines 12, 13)").And.Contain("repeated at least 3 times");
        d.Fingerprints.Should().HaveCount(2);
        d.SuggestedFix!.Summary.Should().StartWith("Load the data Summaries.ForOrderAsync needs for all items before the loop");
    }

    [Fact]
    public void Silent_for_single_shape_or_unrepeated_shapes_or_missing_call_sites()
    {
        using var scope = Synthetic.Scope(o => o.NPlusOneThreshold = 3);
        var site = new CallSite("/a/X.cs", 1, "X.M");
        for (var i = 0; i < 5; i++)
        {
            scope.Add("SELECT * FROM A WHERE Id = @p", parameterHash: "a" + i, callSite: site);   // one shape: QS001's job
        }

        scope.Add("SELECT * FROM B", callSite: site);                                             // a second shape that does not repeat
        for (var i = 0; i < 5; i++)
        {
            scope.Add("SELECT * FROM C WHERE Id = @p", parameterHash: "c" + i);                    // no call site
        }

        new QueryInLoopRule().Analyze(scope).Should().BeEmpty();
    }
}

public class RawSqlConcatenationRuleTests
{
    [Fact]
    public void Fires_when_raw_texts_differ_only_in_literals()
    {
        using var scope = Synthetic.Scope();
        scope.Add("SELECT * FROM Customers WHERE Name = 'Customer 1'", source: QuerySource.Raw, callSite: new CallSite("/a/Search.cs", 5, "Search.Find"));
        scope.Add("SELECT * FROM Customers WHERE Name = 'O''Brien'", source: QuerySource.Raw);
        scope.Add("SELECT * FROM Customers WHERE Name = 'Customer 3'", source: QuerySource.Raw);
        scope.Add("SELECT * FROM Customers WHERE Id = 7", source: QuerySource.Raw);   // different structure, single execution

        var d = new RawSqlConcatenationRule().Analyze(scope).Should().ContainSingle().Subject;
        d.RuleId.Should().Be("QS010");
        d.Severity.Should().Be(Severity.Error);
        d.Title.Should().Be("Raw SQL built from values: 3 text variants of \"SELECT * FROM Customers WHERE Name = ?\" at Search.cs:5 Search.Find");
        d.Explanation.Should().Contain("SQL injection");
        d.Fingerprints.Should().HaveCount(3);
        d.SuggestedFix!.AfterSnippet.Should().Be("SELECT * FROM Customers WHERE Name = {0}");
    }

    [Fact]
    public void Silent_for_parameterized_raw_sql_and_for_linq()
    {
        using var scope = Synthetic.Scope();
        scope.Add("SELECT * FROM Customers WHERE Name = @p0", parameterHash: "a", source: QuerySource.Raw);
        scope.Add("SELECT * FROM Customers WHERE Name = @p0", parameterHash: "b", source: QuerySource.Raw);
        scope.Add("SELECT * FROM Orders WHERE Status = 1");
        scope.Add("SELECT * FROM Orders WHERE Status = 2");
        new RawSqlConcatenationRule().Analyze(scope).Should().BeEmpty();
    }

    [Fact]
    public void Structure_strips_literals_but_keeps_identifiers_and_parameters()
    {
        RawSqlConcatenationRule.Structure("SELECT [t0].[Id] FROM [T1] AS [t0] WHERE [t0].[A] = 'x' AND [t0].[B] = 12.5 AND [t0].[C] = @p0 AND [t0].[D2] = 3")
            .Should().Be("SELECT [t0].[Id] FROM [T1] AS [t0] WHERE [t0].[A] = ? AND [t0].[B] = ? AND [t0].[C] = @p0 AND [t0].[D2] = ?");
    }
}

public class RowLimitingWithoutOrderByRuleTests
{
    [Fact]
    public void Fires_from_ef_core_warning_with_order_by_fix()
    {
        using var scope = Synthetic.Scope();
        var q = Synthetic.Query("DbSet<Order>()\n    .Skip(@__p_0)\n    .Take(@__p_1)", "Order", hasLimit: true);
        q.AddWarning("RowLimitingOperationWithoutOrderBy");
        scope.Add("SELECT * FROM Orders LIMIT @p0 OFFSET @p1", query: q, rows: 5, callSite: new CallSite("/a/Orders.cs", 7, "Orders.Page"));

        var d = new RowLimitingWithoutOrderByRule().Analyze(scope).Should().ContainSingle().Subject;
        d.RuleId.Should().Be("QS011");
        d.Severity.Should().Be(Severity.Warning);
        d.Title.Should().Be("Non-deterministic paging: Skip/Take without OrderBy on Order at Orders.cs:7 Orders.Page");
        d.Explanation.Should().Contain("no inherent order").And.Contain("RowLimitingOperationWithoutOrderByWarning");
        d.SuggestedFix!.Summary.Should().Be("Add .OrderBy(o => o.Id) (or an order that matches the UI) before the Skip/Take");
        d.SuggestedFix.AfterSnippet.Should().Be("DbSet<Order>()\n    .OrderBy(o => o.Id)\n    .Skip(@__p_0)\n    .Take(@__p_1)");
        d.Evidence.Details!["efCoreWarning"].Should().Be("RowLimitingOperationWithoutOrderBy");
    }

    [Fact]
    public void First_without_order_or_filter_is_reported_and_ordered_queries_are_not()
    {
        using var scope = Synthetic.Scope();
        var first = Synthetic.Query("DbSet<Order>()\n    .First()", "Order", hasLimit: true);
        first.AddWarning("FirstWithoutOrderByAndFilter");
        scope.Add("SELECT * FROM Orders LIMIT 1", query: first);
        scope.Add("SELECT * FROM Orders ORDER BY Id LIMIT 1", query: Synthetic.Query("DbSet<Order>()\n    .OrderBy(o => o.Id)\n    .First()", "Order", hasLimit: true));

        var d = new RowLimitingWithoutOrderByRule().Analyze(scope).Should().ContainSingle().Subject;
        d.Title.Should().StartWith("Arbitrary row: First/Single on Order without OrderBy or filter");
    }

    [Fact]
    public void Patch_inserts_order_by_before_the_row_limiting_operator()
    {
        var file = Path.Combine(Path.GetTempPath(), $"qs-{Guid.NewGuid():N}.cs");
        File.WriteAllLines(file, ["var page = await db.Orders.Skip(page * size).Take(size).ToListAsync();"]);
        try
        {
            using var scope = Synthetic.Scope();
            var q = Synthetic.Query("DbSet<Order>()\n    .Skip(@__p_0)\n    .Take(@__p_1)", "Order", hasLimit: true);
            q.AddWarning("RowLimitingOperationWithoutOrderBy");
            scope.Add("SELECT * FROM Orders LIMIT @p0 OFFSET @p1", query: q, callSite: new CallSite(file, 1, "X.M"));

            new RowLimitingWithoutOrderByRule().Analyze(scope).Single().SuggestedFix!.UnifiedDiff.Should()
                .Contain("+var page = await db.Orders.OrderBy(o => o.Id).Skip(page * size).Take(size).ToListAsync();");
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class MissingSplitQueryWithEfWarningTests
{
    [Fact]
    public void Ef_core_warning_makes_a_large_result_a_candidate_even_without_observed_multiplication()
    {
        using var scope = Synthetic.Scope(o => o.CartesianMinimumRows = 50);
        var q = new QueryInfo
        {
            Expression = "DbSet<Customer>()\n    .Include(c => c.Orders)\n    .Include(c => c.Addresses)",
            ExpressionHash = "inc-warned",
            RootEntityShortName = "Customer",
            ReturnsEntities = true,
            CollectionIncludes = ["Orders", "Addresses"],
        };
        q.AddWarning("MultipleCollectionInclude");
        scope.Add("SELECT ... LEFT JOIN ... LEFT JOIN ...", query: q, rows: 80);   // roots unknown: first column was not the key

        var d = new MissingSplitQueryRule().Analyze(scope).Should().ContainSingle().Subject;
        d.Evidence.Details!["efCoreWarning"].Should().Be("MultipleCollectionInclude");
        d.Evidence.Details["distinctRoots"].Should().Be("unknown");
    }
}
