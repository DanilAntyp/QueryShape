using Microsoft.EntityFrameworkCore;

namespace QueryShape.SampleApp;

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public List<Order> Orders { get; set; } = [];
    public List<Address> Addresses { get; set; } = [];
}

public sealed class Address
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string City { get; set; } = string.Empty;
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
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}

public sealed class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>().HasMany(c => c.Orders).WithOne(o => o.Customer).HasForeignKey(o => o.CustomerId);
        modelBuilder.Entity<Customer>().HasMany(c => c.Addresses).WithOne(a => a.Customer).HasForeignKey(a => a.CustomerId);
        modelBuilder.Entity<Order>().HasMany(o => o.Lines).WithOne(l => l.Order).HasForeignKey(l => l.OrderId);
        modelBuilder.Entity<OrderLine>().HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId);
    }

    /// <summary>Seeds a deterministic shop: 40 customers, 5 orders each, 3 lines per order, 2 addresses per customer, 20 products.</summary>
    public static void Seed(ShopDbContext db)
    {
        if (db.Products.Any())
        {
            return;
        }

        var products = Enumerable.Range(1, 20).Select(i => new Product { Sku = $"SKU-{i:000}", Name = $"Product {i}", Price = i * 2.5m }).ToList();
        db.Products.AddRange(products);

        var countries = new[] { "DE", "FR", "NL", "ES" };
        for (var c = 1; c <= 40; c++)
        {
            var customer = new Customer { Name = $"Customer {c}", Country = countries[c % countries.Length] };
            customer.Addresses.Add(new Address { City = "Home " + c });
            customer.Addresses.Add(new Address { City = "Work " + c });
            for (var o = 0; o < 5; o++)
            {
                var order = new Order { PlacedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(c + o), Total = 10m * (o + 1) };
                for (var l = 0; l < 3; l++)
                {
                    order.Lines.Add(new OrderLine { Product = products[(c + o + l) % products.Count], Quantity = l + 1 });
                }

                customer.Orders.Add(order);
            }

            db.Customers.Add(customer);
        }

        db.SaveChanges();
        db.ChangeTracker.Clear();
    }
}
