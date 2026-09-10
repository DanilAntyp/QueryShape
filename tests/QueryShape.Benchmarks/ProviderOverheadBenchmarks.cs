using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace QueryShape.Benchmarks;

/// <summary>Explicit opt-in provider benchmarks. Uses disposable containers, never an existing database.</summary>
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
public class ProviderOverheadBenchmarks
{
    [Params("PostgreSQL", "SqlServer")]
    public string Provider { get; set; } = "PostgreSQL";
    private MsSqlContainer? _sql;
    private PostgreSqlContainer? _pg;
    private ShopContext _plain = null!;
    private ShopContext _captured = null!;
    private readonly QueryShapeOptions _options = new() { CaptureCallSites = false, ReadSourceFiles = false };

    [GlobalSetup]
    public async Task SetupAsync()
    {
        string connection;
        if (Provider == "SqlServer")
        {
            _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _sql.StartAsync();
            connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_sql.GetConnectionString()) { InitialCatalog = "QueryShapeBenchmarks" }.ConnectionString;
        }
        else
        {
            _pg = new PostgreSqlBuilder("postgres:16-alpine").WithDatabase("queryshape_benchmarks").Build();
            await _pg.StartAsync();
            connection = _pg.GetConnectionString();
        }
        DbContextOptionsBuilder<ShopContext> Builder() => Provider == "SqlServer"
            ? new DbContextOptionsBuilder<ShopContext>().UseSqlServer(connection)
            : new DbContextOptionsBuilder<ShopContext>().UseNpgsql(connection);
        _plain = new ShopContext(Builder().Options);
        _captured = new ShopContext(Builder().UseQueryShape(_options).Options);
        await _plain.Database.EnsureCreatedAsync();
        for (var i = 0; i < 200; i++)
            _plain.Customers.Add(new Customer { Name = "Customer " + i, Orders = Enumerable.Range(0, 5).Select(n => new Order { Total = n }).ToList() });
        await _plain.SaveChangesAsync();
        _plain.ChangeTracker.Clear();
        await ReadAsync(_plain);
        await ReadAsync(_captured);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_plain is not null) await _plain.DisposeAsync();
        if (_captured is not null) await _captured.DisposeAsync();
        if (_sql is not null) await _sql.DisposeAsync();
        if (_pg is not null) await _pg.DisposeAsync();
    }

    private static async Task<int> ReadAsync(ShopContext db)
    {
        var customer = await db.Customers.AsNoTracking().OrderBy(c => c.Id).FirstAsync();
        return (await db.Orders.AsNoTracking().Where(o => o.CustomerId == customer.Id).ToArrayAsync()).Length;
    }

    [Benchmark(Baseline = true)]
    public Task<int> EfCoreOnly() => ReadAsync(_plain);

    /// <summary>Capture and the scope's bookkeeping. Rules do not run here: a scope analyses on dispose only when a listener or a report directory asks it to.</summary>
    [Benchmark]
    public async Task<int> Capture()
    {
        using var scope = QueryShapeScope.Begin("provider-benchmark", _options);
        return await ReadAsync(_captured);
    }

    /// <summary>What a scope costs when something consumes its diagnoses: an OpenTelemetry listener, a test, or QUERYSHAPE_REPORT_DIR.</summary>
    [Benchmark]
    public async Task<int> CaptureAndAnalyze()
    {
        using var scope = QueryShapeScope.Begin("provider-benchmark", _options);
        var rows = await ReadAsync(_captured);
        return rows + scope.Analyze().Count;
    }
}
