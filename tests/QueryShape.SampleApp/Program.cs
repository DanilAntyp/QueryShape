using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QueryShape;
using QueryShape.AspNetCore;
using QueryShape.SampleApp;

var builder = WebApplication.CreateBuilder(args);

// One SQLite database file per app instance (temp directory, deleted on shutdown), pooled connections and WAL:
// requests get their own connections, so concurrent requests really run concurrently (a single shared connection cannot).
var dbPath = Path.Combine(Path.GetTempPath(), $"queryshape-sample-{Guid.NewGuid():N}.db");
var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = true }.ToString();

// --- QueryShape: the whole setup is these three lines (1/3 and 2/3 here, 3/3 below). ---
builder.Services.AddQueryShape(o => { o.CaptureCallSites = builder.Environment.IsDevelopment(); });
builder.Services.AddDbContext<ShopDbContext>(o => o.UseSqlite(connectionString).UseQueryShape());

var app = builder.Build();
app.Lifetime.ApplicationStopped.Register(() =>
{
    SqliteConnection.ClearAllPools();
    foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
    {
        try
        {
            File.Delete(dbPath + suffix);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp file.
        }
    }
});

app.UseQueryShape(o => o.EmitResponseHeader = true);   // 3/3

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
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
