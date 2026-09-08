using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace QueryShape.Testing.Xunit.V3.Tests;

public sealed class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = string.Empty;
}

public sealed class MiniContext(DbContextOptions<MiniContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
}

/// <summary>One in-memory SQLite database with two products, captured by QueryShape.</summary>
public sealed class MiniShop : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public MiniShop()
    {
        _connection.Open();
        using var ctx = Create();
        ctx.Database.EnsureCreated();
        ctx.Products.AddRange(new Product { Sku = "a" }, new Product { Sku = "b" });
        ctx.SaveChanges();
    }

    public MiniContext Create()
        => new(new DbContextOptionsBuilder<MiniContext>().UseSqlite(_connection).UseQueryShape(new QueryShapeOptions { CaptureCallSites = false }).Options);

    public void Dispose() => _connection.Dispose();
}
