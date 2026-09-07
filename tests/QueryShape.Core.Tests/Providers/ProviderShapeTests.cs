using Microsoft.EntityFrameworkCore;
using QueryShape.Core.Tests.TestModel;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace QueryShape.Core.Tests.Providers;

/// <summary>Provider-specific SQL (SQL Server brackets, Postgres bare aliases) must normalize to stable shapes and feed the same rules.</summary>
[Trait("Category", "Integration")]
public sealed class ProviderShapeTests : IAsyncLifetime
{
    private MsSqlContainer? _sqlServer;
    private PostgreSqlContainer? _postgres;

    public async Task InitializeAsync()
    {
        if (!Docker.IsAvailable)
        {
            return;
        }

        _sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await Task.WhenAll(_sqlServer.StartAsync(), _postgres.StartAsync());
    }

    public async Task DisposeAsync()
    {
        if (_sqlServer is not null)
        {
            await _sqlServer.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [IntegrationFact]
    public async Task SqlServer_shapes_and_n_plus_one()
    {
        var options = new QueryShapeOptions { CaptureCallSites = true };
        var builder = new DbContextOptionsBuilder<ShopContext>().UseSqlServer(_sqlServer!.GetConnectionString()).UseQueryShape(options);
        await RunAsync(builder.Options, options, expectedShapeStart: "SELECT [t0].[Id], [t0].[CustomerId], [t0].[PlacedAt], [t0].[Total] FROM [Orders] AS [t0] WHERE [t0].[CustomerId] = @p0");
    }

    [IntegrationFact]
    public async Task Postgres_shapes_and_n_plus_one()
    {
        var options = new QueryShapeOptions { CaptureCallSites = true };
        var builder = new DbContextOptionsBuilder<ShopContext>().UseNpgsql(_postgres!.GetConnectionString()).UseQueryShape(options);
        await RunAsync(builder.Options, options, expectedShapeStart: "SELECT t0.\"Id\", t0.\"CustomerId\", t0.\"PlacedAt\", t0.\"Total\" FROM \"Orders\" AS t0 WHERE t0.\"CustomerId\" = @p0");
    }

    private static async Task RunAsync(DbContextOptions<ShopContext> dbOptions, QueryShapeOptions options, string expectedShapeStart)
    {
        await using (var setup = new ShopContext(dbOptions))
        {
            await setup.Database.EnsureDeletedAsync();
            await setup.Database.EnsureCreatedAsync();
            for (var c = 1; c <= 6; c++)
            {
                var customer = new Customer { Name = $"C{c}", Country = "DE" };
                customer.Orders.Add(new Order { PlacedAt = DateTime.UtcNow, Total = c });
                setup.Customers.Add(customer);
            }

            await setup.SaveChangesAsync();
        }

        using var scope = QueryShapeScope.Begin("provider", options);
        await using var ctx = new ShopContext(dbOptions);
        var customers = await ctx.Customers.ToListAsync();
        foreach (var customer in customers)
        {
            await ctx.Orders.Where(o => o.CustomerId == customer.Id).ToListAsync();
        }

        var orderQueries = scope.Commands.Where(c => c.Query?.RootEntityShortName == "Order").ToList();
        orderQueries.Should().HaveCount(6);
        orderQueries.Select(c => c.Fingerprint).Distinct().Should().ContainSingle();
        orderQueries[0].Shape.Should().StartWith(expectedShapeStart);
        orderQueries[0].RowsReturned.Should().Be(1);
        orderQueries[0].Query!.KeyFilters.Should().ContainSingle().Which.NavigationOnRelated.Should().Be("Orders");

        var diagnoses = scope.Analyze();
        diagnoses.Should().Contain(d => d.RuleId == "QS001" && d.Title.StartsWith("N+1 query: Order by CustomerId executed 6 times"));
        diagnoses.Should().Contain(d => d.RuleId == "QS004" && d.Title.StartsWith("Unbounded query: loads every Customer row (6 rows)"));
    }
}
