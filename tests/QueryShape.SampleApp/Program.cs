using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QueryShape;
using QueryShape.AspNetCore;
using QueryShape.SampleApp;

var builder = WebApplication.CreateBuilder(args);

// One shared in-memory SQLite database for the lifetime of the app.
var connection = new SqliteConnection("DataSource=queryshape-sample;Mode=Memory;Cache=Shared");
connection.Open();
builder.Services.AddSingleton(connection);

// --- QueryShape: the whole setup is these three lines (1/3 and 2/3 here, 3/3 below). ---
builder.Services.AddQueryShape(o => { o.CaptureCallSites = builder.Environment.IsDevelopment(); });
builder.Services.AddDbContext<ShopDbContext>((sp, o) => o.UseSqlite(sp.GetRequiredService<SqliteConnection>()).UseQueryShape());

var app = builder.Build();

app.UseQueryShape(o => o.EmitResponseHeader = true);   // 3/3

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
    db.Database.EnsureCreated();
    ShopDbContext.Seed(db);
}

BadEndpoints.Map(app);
GoodEndpoints.Map(app);

app.MapGet("/", () => Results.Text(
    "QueryShape sample. Try: /bad/n-plus-one, /bad/cartesian-explosion, /bad/client-evaluation, /bad/unbounded, /bad/tracking-read-only, " +
    "/bad/missing-split-query, /bad/contains-large-collection, /bad/duplicate-query, /bad/query-in-loop, /bad/raw-sql-concat, /bad/take-without-order-by, and the /good/* twins."));

app.Run();

/// <summary>Exposed so tests can use WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
