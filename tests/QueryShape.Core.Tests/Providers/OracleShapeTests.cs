using Microsoft.EntityFrameworkCore;
using QueryShape.Core.Tests.TestModel;
using Testcontainers.Oracle;
using Xunit.Abstractions;

namespace QueryShape.Core.Tests.Providers;

/// <summary>
/// Oracle speaks a dialect of its own: quoted upper-case identifiers, <c>:p0</c> bind variables, <c>FETCH FIRST</c> instead of <c>LIMIT</c>.
/// Its container is measured in gigabytes and takes minutes to be ready, so this lives apart from the other providers and runs on request
/// (<c>QUERYSHAPE_ORACLE=1</c>, the manual provider workflow).
/// </summary>
[Trait("Category", "Integration")]
public sealed class OracleShapeTests(ITestOutputHelper output) : IAsyncLifetime
{
    private OracleContainer? _oracle;

    public async Task InitializeAsync()
    {
        if (!Docker.IsAvailable || Environment.GetEnvironmentVariable("QUERYSHAPE_ORACLE") is not ("1" or "true"))
        {
            return;
        }

        _oracle = new OracleBuilder("gvenzl/oracle-free:23-slim-faststart").Build();
        await _oracle.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_oracle is not null)
        {
            await _oracle.DisposeAsync();
        }
    }

    [OracleFact]
    public async Task Oracle_shapes_and_n_plus_one()
    {
        var options = new QueryShapeOptions { CaptureCallSites = true };
        var dbOptions = new DbContextOptionsBuilder<ShopContext>()
            .UseOracle(_oracle!.GetConnectionString())
            .UseQueryShape(options)
            .Options;

        await using (var setup = new ShopContext(dbOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            for (var c = 1; c <= 6; c++)
            {
                var customer = new Customer { Name = $"C{c}", Country = "DE" };
                customer.Orders.Add(new Order { PlacedAt = DateTime.UtcNow, Total = c });
                setup.Customers.Add(customer);
            }

            await setup.SaveChangesAsync();
        }

        using var scope = QueryShapeScope.Begin("oracle", options);
        await using var ctx = new ShopContext(dbOptions);
        var customers = await ctx.Customers.ToListAsync();
        foreach (var customer in customers)
        {
            await ctx.Orders.Where(o => o.CustomerId == customer.Id).ToListAsync();
        }

        foreach (var command in scope.Commands)
        {
            output.WriteLine($"[{command.Sequence}] source={command.Source} root={command.Query?.RootEntityShortName ?? "<uncorrelated>"} rows={command.RowsReturned?.ToString() ?? "-"} {command.Shape}");
        }

        customers.Should().HaveCount(6, "the seeding block committed six customers");
        var orderQueries = scope.Commands.Where(c => c.Query?.RootEntityShortName == "Order").ToList();
        orderQueries.Should().HaveCount(6);
        output.WriteLine("shape: " + orderQueries[0].Shape);
        orderQueries.Select(c => c.Fingerprint).Distinct().Should().ContainSingle("six executions differing only by argument are one shape");
        orderQueries[0].RowsReturned.Should().Be(1);
        orderQueries[0].Query!.KeyFilters.Should().ContainSingle().Which.NavigationOnRelated.Should().Be("Orders");
        // Oracle omits AS before a table alias, so its aliases stay as EF generated them (t0, t1); parameters are canonicalized like everywhere else.
        orderQueries[0].Shape.Should().Be("SELECT \"t0\".\"Id\", \"t0\".\"CustomerId\", \"t0\".\"PlacedAt\", \"t0\".\"Total\" FROM \"Orders\" \"t0\" WHERE \"t0\".\"CustomerId\" = @p0");

        var diagnoses = scope.Analyze();
        diagnoses.Should().Contain(d => d.RuleId == "QS001" && d.Title.StartsWith("N+1 query: Order by CustomerId executed 6 times"));
        diagnoses.Should().Contain(d => d.RuleId == "QS004" && d.Title.StartsWith("Unbounded query: loads every Customer row (6 rows)"));
    }
}
