using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.eShopWeb;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Services;
using Microsoft.eShopWeb.Infrastructure.Data;
using Microsoft.eShopWeb.Infrastructure.Data.Queries;
using Microsoft.eShopWeb.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using QueryShape;
using QueryShape.Testing;
using Xunit;
using Xunit.Abstractions;

public class ScenarioTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("catalog-products")]
    [InlineData("catalog-lookups")]
    [InlineData("basket")]
    [InlineData("basket-count")]
    public async Task Scale_real_services(string operation)
    {
        var report = await QueryScaling.RunAsync(Scenario(operation), [10, 100, 501], new ScalingOptions
        {
            Dimension = operation == "catalog-lookups" ? "brands-and-types-each" : "products",
            ConstantRows = operation != "basket",
            MaxCommands = operation.StartsWith("catalog", StringComparison.Ordinal) ? 4 : operation == "basket" ? 2 : 1,
            MaxRowsPerItem = operation == "basket" ? 2 : null,
        });
        output.WriteLine(report.ToText());
        // A deliberately strict constant-row contract exposes the full lookup-list reads.
        // The validation test passes when QueryShape detects that expected violation.
        if (operation == "catalog-lookups")
        {
            Assert.False(report.Passed);
            Assert.Contains("Rows read change with dataset size or repetition.", report.Violations);
        }
        else report.AssertSatisfied();
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(501)]
    public async Task No_tracking_preserves_catalog_behavior(int size)
    {
        var comparison = await QueryEquivalence.CompareAsync(Scenario("catalog-products"),
            Scenario("catalog-products", noTracking: true), size);
        comparison.AssertEquivalent();
        var before = await Scenario("catalog-products").RunAsync(size, $"catalog-tracking/{size}");
        var after = await Scenario("catalog-products", noTracking: true).RunAsync(size, $"catalog-no-tracking/{size}");
        Assert.True(before.Complete && after.Complete);
        Assert.Equal(before.ResultDigest, after.ResultDigest);
        Assert.Equal(before.StateBeforeDigest, after.StateBeforeDigest);
        Assert.Equal(before.StateAfterDigest, after.StateAfterDigest);
        Assert.Equal(before.StateBeforeDigest, before.StateAfterDigest);
        Assert.Contains(before.Report.Diagnostics, d => d.RuleId == "QS005");
        Assert.DoesNotContain(after.Report.Diagnostics, d => d.RuleId == "QS005");
        using var summary = QueryShapeScope.Begin($"catalog-equivalence/{size}");
        summary.Annotate("equivalence.report", JsonSerializer.Serialize(comparison));
        output.WriteLine(JsonSerializer.Serialize(comparison));
    }

    [Fact]
    public async Task Reduce_large_basket_warning()
    {
        var result = await QueryReduction.MinimizeAsync("eshop-basket-QS007", 1000,
            SmallerSizes, n => n, async (n, ct) => await BasketWarningAsync(n, ct)
                ? ReductionTrial.Fail("QS007") : ReductionTrial.Pass,
            new ReductionOptions { ReplayMethod = "ScenarioTests.BasketWarningStillReproducesAsync" });
        output.WriteLine(result.Summary.ToText());
        Assert.True(result.Summary.Complete);
        Assert.Equal(501, result.Input);
        Assert.False(await BasketWarningAsync(500, CancellationToken.None));
    }

    public static Task<bool> BasketWarningStillReproducesAsync(string inputJson, CancellationToken ct) =>
        BasketWarningAsync(JsonSerializer.Deserialize<int>(inputJson), ct);

    private static async Task<bool> BasketWarningAsync(int size, CancellationToken ct)
    {
        var run = await Scenario("basket").RunAsync(size, $"basket-reduction/{size}", ct);
        Assert.True(run.Complete);
        return run.Report.Diagnostics.Any(d => d.RuleId == "QS007");
    }

    private static IEnumerable<int> SmallerSizes(int size)
    {
        for (var decrement = size / 2; decrement >= 1; decrement /= 2)
            yield return size - decrement;
    }

    private static QueryScenario<int, Fixture, object> Scenario(string operation, bool noTracking = false) => new()
    {
        Name = "eshop-" + operation,
        PrepareAsync = (size, ct) => Fixture.CreateAsync(size, operation, noTracking, ct),
        ExecuteAsync = async (f, ct) =>
        {
            var db = f.Db;
            var items = new EfRepository<CatalogItem>(db);
            var uri = new UriComposer(new CatalogSettings { CatalogBaseUrl = "https://example.invalid" });
            if (operation.StartsWith("catalog", StringComparison.Ordinal))
            {
                var service = new CatalogViewModelService(NullLoggerFactory.Instance, items,
                    new EfRepository<CatalogBrand>(db), new EfRepository<CatalogType>(db), uri);
                var page = await service.GetCatalogItems(0, 4, null, null);
                Assert.Equal(4, page.CatalogItems.Count);
                Assert.Equal(f.ProductCount, page.PaginationInfo!.TotalItems);
                return page;
            }
            if (operation == "basket-count")
            {
                var count = await new BasketQueryService(db).CountTotalBasketItems("test-buyer");
                Assert.Equal(f.ProductCount * 2, count);
                return count;
            }
            var basket = await new BasketViewModelService(new EfRepository<Basket>(db), items, uri,
                new BasketQueryService(db)).GetOrCreateBasketForUser("test-buyer");
            Assert.Equal(f.ProductCount, basket.Items.Count);
            return basket;
        },
        ObserveStateAsync = async (f, ct) => await f.Db.CatalogItems.AsNoTracking().OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.Name, p.Price, p.CatalogBrandId, p.CatalogTypeId }).ToArrayAsync(ct),
    };

    private sealed class Fixture(SqliteConnection connection, ShopTests.SqliteCatalogContext db, int productCount) : IAsyncDisposable
    {
        public ShopTests.SqliteCatalogContext Db { get; } = db;
        public int ProductCount { get; } = productCount;

        public static async Task<Fixture> CreateAsync(int size, string operation, bool noTracking, CancellationToken ct)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(ct);
            try
            {
                var options = new DbContextOptionsBuilder<CatalogContext>().UseSqlite(connection)
                    .UseQueryShape(new QueryShapeOptions { CaptureCallSites = true }).Options;
                var productCount = operation == "catalog-lookups" ? 12 : size;
                await using (var seed = new ShopTests.SqliteCatalogContext(options))
                {
                    await seed.Database.EnsureCreatedAsync(ct);
                    var brands = Enumerable.Range(1, operation == "catalog-lookups" ? size : 6)
                        .Select(i => new CatalogBrand($"Brand {i}")).ToList();
                    var types = Enumerable.Range(1, operation == "catalog-lookups" ? size : 5)
                        .Select(i => new CatalogType($"Type {i}")).ToList();
                    seed.AddRange(brands);
                    seed.AddRange(types);
                    await seed.SaveChangesAsync(ct);
                    var products = Enumerable.Range(1, productCount)
                        .Select(i => new CatalogItem(types[0].Id, brands[0].Id, "Test product", $"Product {i}", 10m, "product.png")).ToList();
                    seed.AddRange(products);
                    await seed.SaveChangesAsync(ct);
                    var basket = new Basket("test-buyer");
                    foreach (var product in products) basket.AddItem(product.Id, product.Price, 2);
                    seed.Add(basket);
                    await seed.SaveChangesAsync(ct);
                }
                var db = new ShopTests.SqliteCatalogContext(options);
                if (noTracking) db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
                return new Fixture(connection, db, productCount);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
