using Microsoft.EntityFrameworkCore;

namespace QueryShape.SampleApp;

/// <summary>One deliberately bad endpoint per rule. Each is written the way the pathology shows up in real code.</summary>
public static class BadEndpoints
{
    public static void Map(WebApplication app)
    {
        // QS001: one query per customer.
        app.MapGet("/bad/n-plus-one", async (ShopDbContext db) =>
        {
            var customers = await db.Customers.ToListAsync();
            var result = new List<object>();
            foreach (var customer in customers)
            {
                var orders = await db.Orders.Where(o => o.CustomerId == customer.Id).ToListAsync();
                result.Add(new { customer.Name, Orders = orders.Count });
            }

            return Results.Ok(result);
        });

        // QS002: two collection includes in a single query -> rows = customers x orders x addresses.
        app.MapGet("/bad/cartesian-explosion", async (ShopDbContext db) =>
        {
            var customers = await db.Customers
                .Include(c => c.Orders).ThenInclude(o => o.Lines)
                .Include(c => c.Addresses)
                .ToListAsync();
            return Results.Ok(customers.Select(c => new { c.Name, Orders = c.Orders.Count, Lines = c.Orders.Sum(o => o.Lines.Count), Addresses = c.Addresses.Count }));
        });

        // QS003: a user method in the projection runs in memory for every row.
        app.MapGet("/bad/client-evaluation", async (ShopDbContext db) =>
        {
            var rows = await db.Orders
                .Where(o => o.Total > 0)
                .Select(o => new { o.Id, Label = PriceFormatter.Format(o.Total) })
                .ToListAsync();
            return Results.Ok(rows);
        });

        // QS004: no filter, no limit.
        app.MapGet("/bad/unbounded", async (ShopDbContext db) =>
        {
            var lines = await db.OrderLines.ToListAsync();
            return Results.Ok(new { Count = lines.Count });
        });

        // QS005: tracked entities that are only read.
        app.MapGet("/bad/tracking-read-only", async (ShopDbContext db) =>
        {
            var products = await db.Products.Where(p => p.Price > 10).OrderBy(p => p.Sku).Take(10).ToListAsync();
            return Results.Ok(products.Select(p => new { p.Sku, p.Price }));
        });

        // QS006: several collection includes without AsSplitQuery (mild multiplication: 2 recent orders x 2 addresses per customer).
        app.MapGet("/bad/missing-split-query", async (ShopDbContext db) =>
        {
            var customers = await db.Customers
                .Where(c => c.Id <= 10)
                .Include(c => c.Orders.Where(o => o.Total >= 40))
                .Include(c => c.Addresses)
                .ToListAsync();
            return Results.Ok(customers.Select(c => new { c.Name, Orders = c.Orders.Count, Addresses = c.Addresses.Count }));
        });

        // QS007: Contains over a large in-memory collection.
        app.MapGet("/bad/contains-large-collection", async (ShopDbContext db) =>
        {
            var ids = Enumerable.Range(1, 600).ToList();
            var lines = await db.OrderLines.Where(l => ids.Contains(l.Id)).ToListAsync();
            return Results.Ok(new { Count = lines.Count });
        });

        // QS008: the same query twice with the same arguments.
        app.MapGet("/bad/duplicate-query", async (ShopDbContext db) =>
        {
            var country = "DE";
            var count = await db.Customers.Where(c => c.Country == country).CountAsync();
            var again = await db.Customers.Where(c => c.Country == country).CountAsync();
            return Results.Ok(new { count, again });
        });

        // QS009: one call site (a helper) issuing different queries in a loop.
        app.MapGet("/bad/query-in-loop", async (ShopDbContext db) =>
        {
            var orders = await db.Orders.OrderBy(o => o.Id).Take(8).ToListAsync();
            var result = new List<object>();
            foreach (var order in orders)
            {
                result.Add(await Summaries.ForOrderAsync(db, order));
            }

            return Results.Ok(result);
        });

        // QS011: paging without an order.
        app.MapGet("/bad/take-without-order-by", async (ShopDbContext db, int page = 1, int size = 10) =>
        {
            var orders = await db.Orders.AsNoTracking().Skip(page * size).Take(size).ToListAsync();
            return Results.Ok(orders.Select(o => new { o.Id, o.Total }));
        });

        // QS010: SQL text built with string interpolation instead of parameters.
        app.MapGet("/bad/raw-sql-concat", async (ShopDbContext db) =>
        {
            var result = new List<object>();
            foreach (var name in new[] { "Customer 1", "Customer 2", "Customer 3" })
            {
#pragma warning disable EF1002 // Risk of vulnerability to SQL injection: that is the point of this endpoint.
                var customers = await db.Customers.FromSqlRaw($"SELECT * FROM Customers WHERE Name = '{name}'").ToListAsync();
#pragma warning restore EF1002
                result.Add(new { name, Found = customers.Count });
            }

            return Results.Ok(result);
        });
    }
}

public static class PriceFormatter
{
    public static string Format(decimal price) => price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " EUR";
}

public static class Summaries
{
    /// <summary>Called in a loop: two different queries from the same call site per iteration.</summary>
    public static async Task<object> ForOrderAsync(ShopDbContext db, Order order)
    {
        var customer = await db.Customers.FirstAsync(c => c.Id == order.CustomerId);
        var lineCount = await db.OrderLines.CountAsync(l => l.OrderId == order.Id);
        return new { order.Id, Customer = customer.Name, Lines = lineCount };
    }
}
