using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QueryShape.Normalization;

namespace QueryShape.Benchmarks;

/// <summary>
/// A typical request: one customer lookup and its orders (2 queries), against an in-memory SQLite database.
/// Compares plain EF Core with QueryShape capturing (unscoped, in a scope, and with call-site capture on).
/// </summary>
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
public class CaptureOverheadBenchmarks
{
    private SqliteConnection _connection = null!;
    private ShopContext _plain = null!;
    private ShopContext _captured = null!;
    private ShopContext _capturedWithCallSites = null!;
    private QueryShapeOptions _options = null!;
    private QueryShapeOptions _optionsWithCallSites = null!;
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using (var setup = new ShopContext(new DbContextOptionsBuilder<ShopContext>().UseSqlite(_connection).Options))
        {
            setup.Database.EnsureCreated();
            for (var c = 1; c <= 200; c++)
            {
                var customer = new Customer { Name = "Customer " + c };
                for (var o = 0; o < 5; o++)
                {
                    customer.Orders.Add(new Order { Total = o });
                }

                setup.Customers.Add(customer);
            }

            setup.SaveChanges();
        }

        _options = new QueryShapeOptions { CaptureCallSites = false };
        _optionsWithCallSites = new QueryShapeOptions { CaptureCallSites = true };
        _plain = new ShopContext(new DbContextOptionsBuilder<ShopContext>().UseSqlite(_connection).Options);
        _captured = new ShopContext(new DbContextOptionsBuilder<ShopContext>().UseSqlite(_connection).UseQueryShape(_options).Options);
        _capturedWithCallSites = new ShopContext(new DbContextOptionsBuilder<ShopContext>().UseSqlite(_connection).UseQueryShape(_optionsWithCallSites).Options);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _plain.Dispose();
        _captured.Dispose();
        _capturedWithCallSites.Dispose();
        _connection.Dispose();
    }

    private static async Task<int> RequestAsync(ShopContext ctx, int id)
    {
        var customer = await ctx.Customers.AsNoTracking().FirstAsync(c => c.Id == id);
        var orders = await ctx.Orders.AsNoTracking().Where(o => o.CustomerId == customer.Id).ToListAsync();
        return orders.Count;
    }

    private int NextId() => (_next++ % 200) + 1;

    [Benchmark(Baseline = true)]
    public Task<int> EfCoreOnly() => RequestAsync(_plain, NextId());

    [Benchmark]
    public Task<int> QueryShape_NoScope() => RequestAsync(_captured, NextId());

    [Benchmark]
    public async Task<int> QueryShape_Scope()
    {
        using var scope = QueryShapeScope.Begin("bench", _options);
        return await RequestAsync(_captured, NextId());
    }

    [Benchmark]
    public async Task<int> QueryShape_Scope_Analyze()
    {
        using var scope = QueryShapeScope.Begin("bench", _options);
        var n = await RequestAsync(_captured, NextId());
        return n + scope.Analyze().Count;
    }

    [Benchmark]
    public async Task<int> QueryShape_Scope_CallSites()
    {
        using var scope = QueryShapeScope.Begin("bench", _optionsWithCallSites);
        return await RequestAsync(_capturedWithCallSites, NextId());
    }
}

/// <summary>The pure-CPU part of capture: normalizing SQL and hashing it.</summary>
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
public class NormalizationBenchmarks
{
    private const string Sql = "SELECT \"o\".\"Id\", \"o\".\"CustomerId\", \"o\".\"Total\", \"c\".\"Name\"\nFROM \"Orders\" AS \"o\"\nINNER JOIN \"Customers\" AS \"c\" ON \"o\".\"CustomerId\" = \"c\".\"Id\"\nWHERE \"o\".\"CustomerId\" = @__id_0 AND \"o\".\"Total\" > @__min_1\nORDER BY \"o\".\"Id\"\nLIMIT @__p_2";

    [Benchmark]
    public string Normalize() => SqlNormalizer.Normalize(Sql).Fingerprint;
}

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<Order> Orders { get; set; } = [];
}

public sealed class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public decimal Total { get; set; }
}

public sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
}
