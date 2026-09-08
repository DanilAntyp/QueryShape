using Microsoft.EntityFrameworkCore;
using QueryShape.Core.Tests.TestModel;
using QueryShape.Testing;

namespace QueryShape.Testing.Tests;

public class ScenarioTests
{
    public sealed class Fixture : IAsyncDisposable
    {
        public SqliteShop Shop { get; }
        public bool Disposed { get; private set; }
        public Fixture(int customers) => Shop = new SqliteShop(customers: customers);
        public ValueTask DisposeAsync() { Disposed = true; Shop.Dispose(); return ValueTask.CompletedTask; }
    }

    internal static QueryScenario<int, Fixture, int[]> Growth(bool batch) => new()
    {
        Name = "CustomerOrders",
        PrepareAsync = (size, _) => Task.FromResult(new Fixture(size)),
        ExecuteAsync = async (fixture, ct) =>
        {
            await using var db = fixture.Shop.CreateContext();
            if (batch) return (await db.Customers.Include(c => c.Orders).ToListAsync(ct)).Select(c => c.Orders.Count).ToArray();
            var customers = await db.Customers.ToListAsync(ct);
            var counts = new List<int>();
            foreach (var customer in customers) counts.Add(await db.Orders.CountAsync(o => o.CustomerId == customer.Id, ct));
            return counts.ToArray();
        },
    };

    [Fact]
    public async Task Scaling_detects_n_plus_one_and_confirms_the_batched_fix()
    {
        var bad = await QueryScaling.RunAsync(Growth(false), [2, 5, 10]);
        bad.Passed.Should().BeFalse();
        bad.Points.Select(p => p.Commands).Should().Equal(3, 3, 6, 6, 11, 11);
        bad.ToText().Should().Contain("Command count changes").And.Contain("ScenarioTests.cs");
        var good = await QueryScaling.RunAsync(Growth(true), [2, 5, 10]);
        good.AssertSatisfied();
        good.Points.Select(p => p.Commands).Should().OnlyContain(n => n == 1);
        good.Points.Select(p => p.Rows).Should().Equal(6, 6, 15, 15, 30, 30);
    }

    [Fact]
    public async Task One_command_can_still_violate_a_row_growth_contract()
    {
        var result = await QueryScaling.RunAsync(Growth(true), [2, 5], new ScalingOptions { ConstantRows = true, MaxRowsPerItem = 2 });
        result.Violations.Should().Contain(v => v.Contains("Rows read change")).And.Contain(v => v.Contains("rows per"));
        Action assertion = result.AssertSatisfied;
        assertion.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Setup_state_reads_and_cleanup_are_excluded_and_fixture_is_disposed_on_failure()
    {
        Fixture? prepared = null;
        var scenario = new QueryScenario<int, Fixture, int>
        {
            Name = "isolated",
            PrepareAsync = (n, _) => Task.FromResult(prepared = new Fixture(n)),
            ExecuteAsync = async (f, ct) => { await using var db = f.Shop.CreateContext(); return await db.Customers.CountAsync(ct); },
            ObserveStateAsync = async (f, ct) => { await using var db = f.Shop.CreateContext(); return await db.Customers.Select(c => c.Id).OrderBy(id => id).ToArrayAsync(ct); },
        };
        var result = await scenario.RunAsync(3);
        result.Commands.Should().Be(1);
        result.StateBeforeDigest.Should().Be(result.StateAfterDigest);
        prepared!.Disposed.Should().BeTrue();
        var failed = new QueryScenario<int, Fixture, int>
        {
            Name = "failing",
            PrepareAsync = (n, _) => Task.FromResult(prepared = new Fixture(n)),
            ExecuteAsync = (_, _) => throw new InvalidOperationException("application failure"),
        };
        await ((Func<Task>)(() => failed.RunAsync(2))).Should().ThrowAsync<InvalidOperationException>();
        prepared!.Disposed.Should().BeTrue();
        QueryShapeScope.Current.Should().BeNull();
    }

    [Fact]
    public async Task Disabled_or_overflowed_capture_cannot_pass_scaling()
    {
        foreach (var enabled in new[] { true, false })
        {
            var scenario = Growth(false);
            scenario.CaptureOptions.Enabled = enabled;
            scenario.CaptureOptions.MaxCommandsPerScope = 1;
            var result = await QueryScaling.RunAsync(scenario, [2, 3]);
            result.Passed.Should().BeFalse();
            result.Violations.Should().Contain(v => v.Contains("incomplete"));
        }
    }

    public sealed record CustomerSeed(int Id, int Orders);
    public sealed record CustomerResult(int Id, int Orders);

    internal static QueryScenario<IReadOnlyList<CustomerSeed>, Fixture, CustomerResult[]> Customers(bool loseEmpty) => new()
    {
        Name = "CustomerList",
        PrepareAsync = async (data, ct) =>
        {
            var fixture = new Fixture(0);
            try
            {
                await using var db = fixture.Shop.CreateContext();
                foreach (var row in data)
                {
                    var customer = new Customer { Id = row.Id, Name = "Customer", Country = "PL" };
                    for (var i = 0; i < row.Orders; i++) customer.Orders.Add(new Order { PlacedAt = new DateTime(2026, 1, 1), Total = 1 });
                    db.Add(customer);
                }
                await db.SaveChangesAsync(ct);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        },
        ExecuteAsync = async (f, ct) =>
        {
            await using var db = f.Shop.CreateContext();
            var query = db.Customers.AsQueryable();
            if (loseEmpty) query = query.Where(c => c.Orders.Any());
            return await query.OrderBy(c => c.Id).Select(c => new CustomerResult(c.Id, c.Orders.Count)).ToArrayAsync(ct);
        },
    };

    [Fact]
    public async Task Equivalence_catches_dropped_customers_and_reduction_keeps_a_small_real_database_reproducer()
    {
        IReadOnlyList<CustomerSeed> data = Enumerable.Range(1, 12).Select(id => new CustomerSeed(id, id == 7 ? 0 : 1)).ToArray();
        var reduced = await QueryReduction.MinimizeAsync("lost-customer", data, QueryReduction.RemoveChunks, d => d.Count,
            async (candidate, ct) =>
            {
                var comparison = await QueryEquivalence.CompareAsync(Customers(false), Customers(true), candidate, ct);
                return comparison.Equivalent ? ReductionTrial.Pass : ReductionTrial.Fail(string.Join(",", comparison.Differences));
            });
        reduced.Summary.Complete.Should().BeTrue();
        reduced.Input.Should().ContainSingle().Which.Should().Be(new CustomerSeed(7, 0));
        var fixedComparison = await QueryEquivalence.CompareAsync(Customers(false), Customers(false), reduced.Input);
        fixedComparison.AssertEquivalent();
    }

    [Fact]
    public async Task State_observation_detects_AsNoTracking_losing_an_update_even_when_the_result_matches()
    {
        QueryScenario<int, Fixture, bool> Update(bool tracking) => new()
        {
            Name = "update",
            PrepareAsync = (n, _) => Task.FromResult(new Fixture(n)),
            ExecuteAsync = async (f, ct) =>
            {
                await using var db = f.Shop.CreateContext();
                var query = tracking ? db.Customers.AsTracking() : db.Customers.AsNoTracking();
                var customer = await query.FirstAsync(c => c.Id == 1, ct);
                customer.Name = "Updated";
                await db.SaveChangesAsync(ct);
                return true;
            },
            ObserveStateAsync = async (f, ct) => { await using var db = f.Shop.CreateContext(); return await db.Customers.OrderBy(c => c.Id).Select(c => new { c.Id, c.Name }).ToArrayAsync(ct); },
        };
        var comparison = await QueryEquivalence.CompareAsync(Update(true), Update(false), 2);
        comparison.Equivalent.Should().BeFalse();
        comparison.Differences.Should().Equal("final-state");
    }

    [Fact]
    public void Behavior_canonicalizes_objects_and_preserves_array_order_and_multiplicity()
    {
        QueryBehavior.Digest(new Dictionary<string, int> { ["b"] = 2, ["a"] = 1 }).Should()
            .Be(QueryBehavior.Digest(new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }));
        QueryBehavior.Digest(new[] { 1, 2 }).Should().NotBe(QueryBehavior.Digest(new[] { 2, 1 }));
        QueryBehavior.Digest(new[] { 1, 2 }, ResultOrder.Ignore).Should().Be(QueryBehavior.Digest(new[] { 2, 1 }, ResultOrder.Ignore));
        QueryBehavior.Digest(new[] { 1, 1, 2 }, ResultOrder.Ignore).Should().NotBe(QueryBehavior.Digest(new[] { 1, 2 }, ResultOrder.Ignore));
        using var scope = QueryShapeScope.Begin("behavior");
        scope.Observe("result", "private-value");
        scope.Annotations.Values.Should().NotContain(v => v.Contains("private-value"));
        Action duplicate = () => scope.Observe("result", "different");
        duplicate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Reduction_skips_invalid_or_different_failures_and_honors_budget_and_cancellation()
    {
        var result = await QueryReduction.MinimizeAsync("signature", 8, n => Enumerable.Range(0, n), n => n,
            (n, _) => Task.FromResult(n == 0 ? ReductionTrial.Invalid : n < 4 ? ReductionTrial.Fail("different") : ReductionTrial.Fail("original")));
        result.Input.Should().Be(4);
        result.Summary.Complete.Should().BeTrue();
        var limited = await QueryReduction.MinimizeAsync("budget", 8, n => Enumerable.Range(0, n), n => n,
            (_, _) => Task.FromResult(ReductionTrial.Fail("original")), new ReductionOptions { MaxAttempts = 2 });
        limited.Input.Should().Be(8);
        limited.Summary.Complete.Should().BeFalse();
        await ((Func<Task>)(() => QueryReduction.MinimizeAsync("cancel", 8, n => Enumerable.Range(0, n), n => n,
            (_, _) => Task.FromResult(ReductionTrial.Pass), cancellationToken: new CancellationToken(true)))).Should().ThrowAsync<OperationCanceledException>();
        await ((Func<Task>)(() => QueryReduction.MinimizeAsync("exception", 8, n => Enumerable.Range(0, n), n => n,
            (_, _) => throw new InvalidOperationException("setup failed")))).Should().ThrowAsync<InvalidOperationException>().WithMessage("setup failed");
    }
}
