using QueryShape.Rules;

namespace QueryShape.Core.Tests.Rules;

public class SynchronousQueryRuleTests
{
    private static QueryShapeScope AsyncHostScope()
    {
        var scope = Synthetic.Scope();
        scope.Annotate(QueryShapeScope.AsyncHostAnnotation, "true");
        return scope;
    }

    [Fact]
    public void Fires_for_a_blocking_query_in_an_async_scope()
    {
        using var scope = AsyncHostScope();
        var q = Synthetic.Query("DbSet<Customer>()\n    .Where(c => c.Country == @__country_0)", "Customer", hasFilter: true);
        scope.Add("SELECT * FROM Customers WHERE Country = @p", query: q, rows: 10, ms: 12,
            callSite: new CallSite("/a/CustomersController.cs", 31, "CustomersController.List"));

        var d = new SynchronousQueryRule().Analyze(scope).Should().ContainSingle().Subject;
        d.RuleId.Should().Be("QS012");
        d.Severity.Should().Be(Severity.Warning);
        d.Title.Should().Be("Blocking query: Customer ran synchronously once in an async request, blocking the thread for 12 ms at CustomersController.cs:31 CustomersController.List");
        d.Explanation.Should().Contain("thread pool").And.NotContain(d.Title);
        d.SuggestedFix!.Summary.Should().StartWith("Await the async overload");
        d.SuggestedFix.UnifiedDiff.Should().BeNull("the expression tree stops before the terminal operator");
        d.SuggestedFix.IsPartial.Should().BeTrue();
        d.SuggestedFix.ManualStep.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Silent_unless_the_host_declared_itself_asynchronous()
    {
        using var scope = Synthetic.Scope();   // a console app, a batch job, a plain unit test
        scope.Add("SELECT * FROM Customers", query: Synthetic.Query("DbSet<Customer>()", "Customer"), rows: 10);

        new SynchronousQueryRule().Analyze(scope).Should().BeEmpty();
    }

    [Fact]
    public void Silent_for_async_commands()
    {
        using var scope = AsyncHostScope();
        scope.Add("SELECT * FROM Customers", query: Synthetic.Query("DbSet<Customer>()", "Customer"), rows: 10, isAsync: true);

        new SynchronousQueryRule().Analyze(scope).Should().BeEmpty();
    }

    [Fact]
    public void Repeated_executions_of_one_shape_are_one_finding_with_the_blocked_time_summed()
    {
        using var scope = AsyncHostScope();
        var q = Synthetic.Query("DbSet<Order>()\n    .Where(o => o.CustomerId == @__id_0)", "Order", hasFilter: true);
        var site = new CallSite("/a/OrderService.cs", 12, "OrderService.Load");
        for (var i = 0; i < 5; i++)
        {
            scope.Add("SELECT * FROM Orders WHERE CustomerId = @p", parameterHash: "p" + i, query: q, rows: 3, ms: 4, callSite: site);
        }

        var d = new SynchronousQueryRule().Analyze(scope).Should().ContainSingle().Subject;
        d.Title.Should().Contain("ran synchronously 5 times").And.Contain("20 ms");
        d.Evidence.Count.Should().Be(5);
        d.Evidence.Details!["asyncCommandsInScope"].Should().Be("0");
    }

    [Fact]
    public void Reports_SaveChanges_in_its_own_words()
    {
        using var scope = AsyncHostScope();
        scope.Add("UPDATE Customers SET Name = @p WHERE Id = @p1", source: QuerySource.SaveChanges,
            method: Microsoft.EntityFrameworkCore.Diagnostics.DbCommandMethod.ExecuteNonQuery, ms: 7);

        var d = new SynchronousQueryRule().Analyze(scope).Should().ContainSingle().Subject;
        d.Title.Should().StartWith("Blocking SaveChanges(): SaveChanges ran synchronously once");
        d.SuggestedFix!.Summary.Should().Be("Call await SaveChangesAsync(cancellationToken) instead of SaveChanges()");
        d.Evidence.Details!["source"].Should().Be("SaveChanges");
    }

    [Fact]
    public void An_inner_scope_inherits_the_hosts_declaration()
    {
        using var request = AsyncHostScope();
        using var inner = QueryShapeScope.Begin("handler", request.Options);
        inner.Add("SELECT * FROM Customers", query: Synthetic.Query("DbSet<Customer>()", "Customer"), rows: 10);

        new SynchronousQueryRule().Analyze(inner).Should().ContainSingle();
    }
}
