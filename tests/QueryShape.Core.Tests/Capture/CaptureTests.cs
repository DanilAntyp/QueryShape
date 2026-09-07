using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using QueryShape.Core.Tests.TestModel;

namespace QueryShape.Core.Tests.Capture;

public class CaptureTests : IDisposable
{
    private readonly SqliteShop _shop = new();

    public void Dispose() => _shop.Dispose();

    [Fact]
    public async Task Captures_linq_query_with_shape_fingerprint_and_expression()
    {
        using var scope = QueryShapeScope.Begin("test", _shop.Options);
        await using var ctx = _shop.CreateContext();

        var country = "DE";
        var customers = await ctx.Customers.Where(c => c.Country == country).ToListAsync();

        customers.Should().HaveCount(5);
        var cmd = scope.Commands.Should().ContainSingle().Subject;
        cmd.Source.Should().Be(QuerySource.Linq);
        cmd.CommandSource.Should().Be(CommandSource.LinqQuery);
        cmd.ExecuteMethod.Should().Be(DbCommandMethod.ExecuteReader);
        cmd.Shape.Should().StartWith("SELECT \"t0\".\"Id\", \"t0\".\"Country\", \"t0\".\"Name\" FROM \"Customers\" AS \"t0\" WHERE \"t0\".\"Country\" = @");
        cmd.Fingerprint.Should().MatchRegex("^[0-9a-f]{12}$");
        cmd.RowsReturned.Should().Be(5);
        cmd.IsAsync.Should().BeTrue();
        cmd.ProviderName.Should().Be("Microsoft.EntityFrameworkCore.Sqlite");
        cmd.ContextId.Should().Be(ctx.ContextId.InstanceId);
        cmd.Parameters.Should().ContainSingle().Which.Value.Should().BeNull("parameter values are off by default");

        cmd.Query.Should().NotBeNull();
        cmd.Query!.RootEntityShortName.Should().Be("Customer");
        cmd.Query.RootTableName.Should().Be("Customers");
        cmd.Query.HasFilter.Should().BeTrue();
        cmd.Query.HasLimit.Should().BeFalse();
        cmd.Query.IsTracking.Should().BeTrue();
        cmd.Query.ReturnsEntities.Should().BeTrue();
        cmd.Query.Expression.Should().Contain("DbSet<Customer>()").And.Contain("Where(");
        cmd.Query.RootKeyProperties.Should().Equal("Id");
    }

    [Fact]
    public async Task Captures_call_site_when_enabled()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        await ctx.Products.CountAsync();

        var cmd = scope.Commands.Should().ContainSingle().Subject;
        cmd.ExecuteMethod.Should().Be(DbCommandMethod.ExecuteReader, "EF Core runs scalar LINQ queries through a reader");
        cmd.RowsReturned.Should().Be(1);
        cmd.CallSite.Should().NotBeNull();
        cmd.CallSite!.Member.Should().Be("CaptureTests.Captures_call_site_when_enabled");
        cmd.CallSite.FileName.Should().Be("CaptureTests.cs");
        cmd.CallSite.Line.Should().BeGreaterThan(0);
        cmd.Query!.HasLimit.Should().BeTrue("Count is an aggregate");
    }

    [Fact]
    public async Task Honors_ef_TagWithCallSite_without_stack_walking()
    {
        var options = new QueryShapeOptions { CaptureCallSites = false };
        using var scope = QueryShapeScope.Begin(options: options);
        await using var ctx = _shop.CreateContext(options);

        await ctx.Products.TagWith("lookup").TagWithCallSite().ToListAsync();

        var cmd = scope.Commands.Should().ContainSingle().Subject;
        cmd.Tags.Should().HaveCount(2).And.Contain("lookup");
        cmd.Tags[1].Should().StartWith("File: ").And.EndWith(".cs:" + cmd.CallSite!.Line);
        cmd.CallSite.Should().NotBeNull();
        cmd.CallSite!.FileName.Should().Be("CaptureTests.cs");
        cmd.CallSite.Line.Should().BeGreaterThan(0);
        cmd.Shape.Should().NotContain("--", "tags are stripped from the shape");
    }

    [Fact]
    public async Task Repeated_executions_reuse_the_compiled_expression_info()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        var ids = await ctx.Customers.Select(c => c.Id).ToListAsync();
        foreach (var id in ids)
        {
            await ctx.Orders.Where(o => o.CustomerId == id).ToListAsync();
        }

        var orderQueries = scope.Commands.Where(c => c.Query?.RootEntityShortName == "Order").ToList();
        orderQueries.Should().HaveCount(10);
        orderQueries.Select(c => c.Fingerprint).Distinct().Should().ContainSingle();
        orderQueries.Select(c => c.ParameterHash).Distinct().Should().HaveCount(10, "each iteration has a different customer id");
        orderQueries.Should().OnlyContain(c => c.Query != null && c.Query.KeyFilters.Count == 1);
        var kf = orderQueries[0].Query!.KeyFilters[0];
        kf.Should().Be(new KeyFilter("Order", "CustomerId", false, "Customer", "Orders", "Customer"));
    }

    [Fact]
    public async Task Tracking_and_no_tracking_variants_of_same_sql_are_distinguished()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        await ctx.Products.ToListAsync();
        await ctx.Products.AsNoTracking().ToListAsync();

        scope.Commands.Should().HaveCount(2);
        scope.Commands[0].Fingerprint.Should().Be(scope.Commands[1].Fingerprint, "AsNoTracking does not change SQL");
        scope.Commands[0].Query!.IsTracking.Should().BeTrue();
        scope.Commands[1].Query!.IsTracking.Should().BeFalse();
    }

    [Fact]
    public async Task Split_query_commands_share_expression_info()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        await ctx.Customers.Include(c => c.Orders).AsSplitQuery().ToListAsync();

        scope.Commands.Should().HaveCount(2);
        scope.Commands.Should().OnlyContain(c => c.Query != null && c.Query.SplittingBehavior == "SplitQuery" && c.Query.CollectionIncludes.Count == 1);
        scope.Commands[0].Query!.CollectionIncludes.Should().Equal("Orders");
    }

    [Fact]
    public async Task Detects_collection_includes_by_lambda_and_string()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        await ctx.Customers.Include(c => c.Orders).ThenInclude(o => o.Lines).ThenInclude(l => l.Product).ToListAsync();
        await ctx.Customers.Include("Orders.Lines.Product").ToListAsync();

        scope.Commands.Should().HaveCount(2);
        scope.Commands[0].Query!.CollectionIncludes.Should().Equal("Orders", "Orders.Lines");
        scope.Commands[1].Query!.CollectionIncludes.Should().Equal("Orders", "Orders.Lines");
    }

    [Fact]
    public async Task Save_changes_is_recorded_with_modified_entity_types()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        var product = await ctx.Products.FirstAsync();
        product.Price += 1;
        await ctx.SaveChangesAsync();

        scope.SaveChanges.Should().ContainSingle().Which.ModifiedEntityTypes.Should().Equal(typeof(Product).FullName!);
        scope.Commands.Should().Contain(c => c.Source == QuerySource.SaveChanges && c.ExecuteMethod == DbCommandMethod.ExecuteReader);
        scope.Commands.Single(c => c.Source == QuerySource.Linq).Query!.HasLimit.Should().BeTrue();
    }

    [Fact]
    public async Task FromSqlRaw_is_captured_as_raw()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        await ctx.Products.FromSqlRaw("SELECT * FROM Products WHERE Price > {0}", 10m).ToListAsync();
        await ctx.Database.ExecuteSqlRawAsync("UPDATE Products SET Price = Price WHERE Id = {0}", 1);

        scope.Commands.Should().HaveCount(2);
        scope.Commands[0].Source.Should().Be(QuerySource.Raw);
        scope.Commands[0].Query.Should().NotBeNull();
        scope.Commands[0].Query!.IsFromSql.Should().BeTrue();
        scope.Commands[1].Source.Should().Be(QuerySource.Raw);
        scope.Commands[1].RowsAffected.Should().Be(1);
        scope.Commands[1].Query.Should().BeNull();
    }

    [Fact]
    public async Task Commands_outside_a_scope_go_to_the_unscoped_buffer()
    {
        // The buffer is process-wide (other test classes run in parallel), so only assert on this context's commands.
        var options = new QueryShapeOptions { UnscopedBufferSize = 64 };
        await using var ctx = _shop.CreateContext(options);
        var interceptor = QueryShape.Capture.QueryShapeInterceptor.Instance;

        QueryShapeScope.Current.Should().BeNull();
        await ctx.Customers.CountAsync();
        await ctx.Orders.CountAsync();

        var mine = interceptor.RecentUnscopedCommands.Where(c => c.ContextId == ctx.ContextId.InstanceId).ToList();
        mine.Select(c => c.Query!.RootEntityShortName).Should().Equal("Customer", "Order");
        interceptor.OptionsFor(ctx).Should().BeSameAs(options);
    }

    [Fact]
    public async Task Scope_stops_recording_at_max_commands_and_flags_overflow()
    {
        var options = new QueryShapeOptions { MaxCommandsPerScope = 3 };
        using var scope = QueryShapeScope.Begin(options: options);
        await using var ctx = _shop.CreateContext(options);

        for (var i = 0; i < 5; i++)
        {
            await ctx.Products.CountAsync();
        }

        scope.Commands.Should().HaveCount(3);
        scope.Overflowed.Should().BeTrue();
        scope.Analyze().Should().Contain(d => d.RuleId == QueryShapeScope.OverflowRuleId);
    }

    [Fact]
    public async Task Scopes_nest_and_flow_across_await()
    {
        await using var ctx = _shop.CreateContext();
        using var outer = QueryShapeScope.Begin("outer", _shop.Options);
        await ctx.Products.CountAsync();

        using (var inner = QueryShapeScope.Begin("inner", _shop.Options))
        {
            await Task.Yield();
            await ctx.Customers.CountAsync();
            QueryShapeScope.Current.Should().BeSameAs(inner);
            inner.Commands.Should().ContainSingle();
        }

        QueryShapeScope.Current.Should().BeSameAs(outer);
        await ctx.Orders.CountAsync();
        outer.Commands.Should().HaveCount(2);
    }

    [Fact]
    public async Task Listener_receives_commands_and_scope_completion()
    {
        var listener = new RecordingListener();
        var options = new QueryShapeOptions { NPlusOneThreshold = 2 };
        options.Listeners.Add(listener);
        await using var ctx = _shop.CreateContext(options);

        using (QueryShapeScope.Begin(options: options))
        {
            foreach (var id in new[] { 1, 2, 3 })
            {
                await ctx.Orders.Where(o => o.CustomerId == id).ToListAsync();
            }
        }

        listener.Commands.Should().HaveCount(3);
        listener.Completed.Should().ContainSingle().Which.Should().Contain(d => d.RuleId == "QS001");
    }

    [Fact]
    public async Task Failed_command_is_recorded_as_failed_and_does_not_break_the_query()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await using var ctx = _shop.CreateContext();

        var act = () => ctx.Database.ExecuteSqlRawAsync("SELECT * FROM NoSuchTable");
        await act.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();

        var cmd = scope.Commands.Should().ContainSingle().Subject;
        cmd.Failed.Should().BeTrue();
        cmd.ErrorType.Should().Be("SqliteException");
    }

    [Fact]
    public async Task Parameter_values_are_kept_only_when_opted_in()
    {
        var options = new QueryShapeOptions { IncludeParameterValues = true };
        using var scope = QueryShapeScope.Begin(options: options);
        await using var ctx = _shop.CreateContext(options);

        var country = "DE";
        await ctx.Customers.Where(c => c.Country == country).ToListAsync();

        scope.Commands.Single().Parameters.Single().Value.Should().Be("DE");
    }

    private sealed class RecordingListener : IQueryShapeListener
    {
        public List<CapturedCommand> Commands { get; } = [];
        public List<IReadOnlyList<Diagnosis>> Completed { get; } = [];

        public void OnCommandCaptured(CapturedCommand command, QueryShapeScope? scope) => Commands.Add(command);

        public void OnScopeCompleted(QueryShapeScope scope, IReadOnlyList<Diagnosis> diagnoses) => Completed.Add(diagnoses);
    }
}
