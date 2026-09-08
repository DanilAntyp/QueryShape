using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.eShopWeb;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Services;
using Microsoft.eShopWeb.ApplicationCore.Specifications;
using Microsoft.eShopWeb.Infrastructure.Data;
using Microsoft.eShopWeb.Infrastructure.Data.Queries;
using Microsoft.eShopWeb.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using QueryShape;
using Xunit;

// Real upstream services, repositories, specifications and entity mappings; only data and wiring live here.
public class ShopTests
{
    [Theory]
    [InlineData("catalog")]
    [InlineData("catalog-large-lookups")]
    [InlineData("basket")]
    [InlineData("basket-501-items")]
    [InlineData("basket-count")]
    [InlineData("checkout")]
    [InlineData("order-history")]
    public async Task Exercise(string scenario)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new QueryShapeOptions { CaptureCallSites = true, ReadSourceFiles = true };
        var dbOptions = new DbContextOptionsBuilder<CatalogContext>().UseSqlite(connection).UseQueryShape(options).Options;
        var largeBasket = scenario == "basket-501-items";
        var largeLookups = scenario == "catalog-large-lookups";
        int basketId;
        await using (var seed = new SqliteCatalogContext(dbOptions))
        {
            await seed.Database.EnsureCreatedAsync();
            var brands = Enumerable.Range(1, largeLookups ? 30 : 6).Select(i => new CatalogBrand($"Brand {i}")).ToList();
            var types = Enumerable.Range(1, largeLookups ? 25 : 5).Select(i => new CatalogType($"Type {i}")).ToList();
            seed.AddRange(brands);
            seed.AddRange(types);
            await seed.SaveChangesAsync();
            var products = Enumerable.Range(1, largeBasket ? 501 : 12)
                .Select(i => new CatalogItem(types[0].Id, brands[0].Id, "Test product", $"Product {i}", 10m, "product.png")).ToList();
            seed.AddRange(products);
            await seed.SaveChangesAsync();
            var basket = new Basket("test-buyer");
            foreach (var product in products.Take(largeBasket ? 501 : 3)) basket.AddItem(product.Id, product.Price, 2);
            seed.Add(basket);
            seed.Add(new Order("test-buyer", new Address("Test", "Test", "Test", "Test", "00000"),
                [new OrderItem(new CatalogItemOrdered(products[0].Id, products[0].Name, products[0].PictureUri), 10m, 2)]));
            await seed.SaveChangesAsync();
            basketId = basket.Id;
        }

        // Repeat with a fresh context and scope to exercise EF's compiled-query cache, too.
        for (var run = 1; run <= 2; run++)
        {
            await using var db = new SqliteCatalogContext(dbOptions);
            var items = new EfRepository<CatalogItem>(db);
            var baskets = new EfRepository<Basket>(db);
            var orders = new EfRepository<Order>(db);
            var uri = new UriComposer(new CatalogSettings { CatalogBaseUrl = "https://example.invalid" });
            using var scope = QueryShapeScope.Begin($"{scenario}/run-{run}", options);
            switch (scenario)
            {
                case "catalog":
                case "catalog-large-lookups":
                    var catalog = new CatalogViewModelService(NullLoggerFactory.Instance, items,
                        new EfRepository<CatalogBrand>(db), new EfRepository<CatalogType>(db), uri);
                    var page = await catalog.GetCatalogItems(0, 4, null, null);
                    Assert.Equal(4, page.CatalogItems.Count);
                    Assert.Equal(12, page.PaginationInfo!.TotalItems);
                    break;
                case "basket":
                case "basket-501-items":
                    var service = new BasketViewModelService(baskets, items, uri, new BasketQueryService(db));
                    var view = await service.GetOrCreateBasketForUser("test-buyer");
                    Assert.Equal(largeBasket ? 501 : 3, view.Items.Count);
                    break;
                case "basket-count":
                    Assert.Equal(6, await new BasketQueryService(db).CountTotalBasketItems("test-buyer"));
                    break;
                case "checkout":
                    await new OrderService(baskets, items, orders, uri).CreateOrderAsync(basketId,
                        new Address("Test", "Test", "Test", "Test", "00000"));
                    Assert.Contains(scope.Commands, c => c.Source == QuerySource.SaveChanges);
                    break;
                case "order-history":
                    var history = await orders.ListAsync(new CustomerOrdersWithItemsSpecification("test-buyer"));
                    Assert.Single(history);
                    Assert.Single(history[0].OrderItems);
                    break;
            }
            Assert.NotEmpty(scope.Commands);
            Assert.All(scope.Commands.Where(c => c.Source == QuerySource.Linq), c => Assert.NotNull(c.Query));
            var diagnoses = scope.Analyze();
            Assert.DoesNotContain(diagnoses, d => d.Severity == Severity.Error);
            var expectedRules = scenario switch
            {
                "catalog" or "catalog-large-lookups" => new[] { "QS004", "QS005", "QS011" },
                "basket-501-items" => ["QS005", "QS007"],
                "basket-count" => [],
                _ => ["QS005"],
            };
            Assert.Equal(expectedRules, diagnoses.Select(d => d.RuleId).Distinct().OrderBy(id => id).ToArray());
        }
    }

    internal sealed class SqliteCatalogContext(DbContextOptions<CatalogContext> options) : CatalogContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            // Upstream uses SQL Server HiLo sequences. Adapt only key generation for SQLite.
            foreach (var sequence in builder.Model.GetSequences().ToList())
                builder.Model.RemoveSequence(sequence.Name, sequence.Schema);
            foreach (var entity in builder.Model.GetEntityTypes())
            {
                var id = entity.FindProperty("Id");
                if (id?.ClrType == typeof(int))
                {
                    id.SetValueGenerationStrategy(null);
                    id.SetHiLoSequenceName(null);
                    id.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd;
                }
            }
        }
    }
}
