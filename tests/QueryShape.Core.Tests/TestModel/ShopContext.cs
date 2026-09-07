using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace QueryShape.Core.Tests.TestModel;

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public List<Order> Orders { get; set; } = [];
}

public sealed class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public DateTime PlacedAt { get; set; }
    public decimal Total { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
}

public sealed class OrderLine
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public int Quantity { get; set; }
}

public sealed class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public decimal Price { get; set; }
}

public sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>().HasMany(c => c.Orders).WithOne(o => o.Customer).HasForeignKey(o => o.CustomerId);
        modelBuilder.Entity<Order>().HasMany(o => o.Lines).WithOne(l => l.Order).HasForeignKey(l => l.OrderId);
        modelBuilder.Entity<OrderLine>().HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId);
    }
}

/// <summary>One open SQLite in-memory database per fixture, seeded with a small shop.</summary>
public sealed class SqliteShop : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteShop(int customers = 10, int ordersPerCustomer = 3, int linesPerOrder = 2, Action<QueryShapeOptions>? configure = null)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        Options = new QueryShapeOptions { CaptureCallSites = true };
        configure?.Invoke(Options);

        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();
        Seed(ctx, customers, ordersPerCustomer, linesPerOrder);
    }

    public QueryShapeOptions Options { get; }

    public SqliteConnection Connection => _connection;

    /// <summary>Creates a context capturing with <paramref name="options"/> (defaults to the fixture's options).</summary>
    public ShopContext CreateContext(QueryShapeOptions? options = null)
    {
        var builder = new DbContextOptionsBuilder<ShopContext>().UseSqlite(_connection).UseQueryShape(options ?? Options);
        return new ShopContext(builder.Options);
    }

    private static void Seed(ShopContext ctx, int customers, int ordersPerCustomer, int linesPerOrder)
    {
        var products = Enumerable.Range(1, 5).Select(i => new Product { Sku = $"SKU-{i}", Price = i * 10m }).ToList();
        ctx.Products.AddRange(products);
        for (var c = 1; c <= customers; c++)
        {
            var customer = new Customer { Name = $"Customer {c}", Country = c % 2 == 0 ? "DE" : "FR" };
            for (var o = 0; o < ordersPerCustomer; o++)
            {
                var order = new Order { PlacedAt = new DateTime(2026, 1, 1).AddDays(o), Total = 100 * (o + 1) };
                for (var l = 0; l < linesPerOrder; l++)
                {
                    order.Lines.Add(new OrderLine { Product = products[(c + o + l) % products.Count], Quantity = l + 1 });
                }

                customer.Orders.Add(order);
            }

            ctx.Customers.Add(customer);
        }

        ctx.SaveChanges();
        ctx.ChangeTracker.Clear();
    }

    public void Dispose() => _connection.Dispose();
}
