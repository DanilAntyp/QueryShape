using Microsoft.EntityFrameworkCore;

namespace QueryShape.SampleApp;

/// <summary>The fixed twins of the bad endpoints, used by the README and by `dotnet queryshape verify`.</summary>
public static class GoodEndpoints
{
    public static void Map(WebApplication app)
    {
        // A parameterized route: the request scope is named by the template, not by each id.
        app.MapGet("/good/customer/{id:int}", async (ShopDbContext db, int id) =>
        {
            var customer = await db.Customers.Include(c => c.Orders).AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            return customer is null ? Results.NotFound() : Results.Ok(new { customer.Name, Orders = customer.Orders.Count });
        });

        app.MapGet("/good/n-plus-one", async (ShopDbContext db) =>
        {
            var customers = await db.Customers.Include(c => c.Orders).AsNoTracking().ToListAsync();
            return Results.Ok(customers.Select(c => new { c.Name, Orders = c.Orders.Count }));
        });

        app.MapGet("/good/cartesian-explosion", async (ShopDbContext db) =>
        {
            var customers = await db.Customers
                .Include(c => c.Orders).ThenInclude(o => o.Lines)
                .Include(c => c.Addresses)
                .AsSplitQuery()
                .AsNoTracking()
                .ToListAsync();
            return Results.Ok(customers.Select(c => new { c.Name, Orders = c.Orders.Count, Addresses = c.Addresses.Count }));
        });

        app.MapGet("/good/client-evaluation", async (ShopDbContext db) =>
        {
            var raw = await db.Orders.Where(o => o.Total > 0).Select(o => new { o.Id, o.Total }).ToListAsync();
            return Results.Ok(raw.Select(o => new { o.Id, Label = PriceFormatter.Format(o.Total) }));
        });

        app.MapGet("/good/unbounded", async (ShopDbContext db, int page = 0, int size = 50) =>
        {
            var lines = await db.OrderLines.AsNoTracking().OrderBy(l => l.Id).Skip(page * size).Take(size).ToListAsync();
            return Results.Ok(new { Count = lines.Count });
        });

        app.MapGet("/good/tracking-read-only", async (ShopDbContext db) =>
        {
            var products = await db.Products.AsNoTracking().Where(p => p.Price > 10).OrderBy(p => p.Sku).Take(10).ToListAsync();
            return Results.Ok(products.Select(p => new { p.Sku, p.Price }));
        });

        app.MapGet("/good/duplicate-query", async (ShopDbContext db) =>
        {
            var country = "DE";
            var count = await db.Customers.Where(c => c.Country == country).CountAsync();
            return Results.Ok(new { count, again = count });
        });

        app.MapGet("/good/take-without-order-by", async (ShopDbContext db, int page = 1, int size = 10) =>
        {
            var orders = await db.Orders.AsNoTracking().OrderBy(o => o.PlacedAt).ThenBy(o => o.Id).Skip(page * size).Take(size).ToListAsync();
            return Results.Ok(orders.Select(o => new { o.Id, o.Total }));
        });

        app.MapGet("/good/raw-sql-concat", async (ShopDbContext db) =>
        {
            var result = new List<object>();
            foreach (var name in new[] { "Customer 1", "Customer 2", "Customer 3" })
            {
                var customers = await db.Customers.FromSqlInterpolated($"SELECT * FROM Customers WHERE Name = {name}").AsNoTracking().ToListAsync();
                result.Add(new { name, Found = customers.Count });
            }

            return Results.Ok(result);
        });

        app.MapGet("/good/sync-query", async (ShopDbContext db) =>
        {
            var customers = await db.Customers.AsNoTracking().Where(c => c.Country == "DE").ToListAsync();
            return Results.Ok(customers.Select(c => new { c.Id, c.Name }));
        });
    }
}
