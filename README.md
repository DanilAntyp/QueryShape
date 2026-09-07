# QueryShape

**Catches slow and dangerous Entity Framework Core queries, explains them in terms of the LINQ that produced them, and stops regressions from reaching production.**

- **Query snapshot testing** — a test fails in CI when a code change makes EF Core issue more or different queries. Jest snapshots, but for SQL.
- **Diagnosis + fix** — every finding says *why* EF Core did it and comes with a concrete, applicable fix (`Add .Include(c => c.Orders) at OrderService.cs:42`), with a unified diff when the call site is known.
- **OpenTelemetry enrichment** — findings land on the spans you already look at as `queryshape.*` tags and `queryshape.diagnosis` events, plus a `QueryShape` meter. No dependency on the OpenTelemetry SDK.

Targets .NET 8 / EF Core 8 and .NET 10 / EF Core 10. SQLite, SQL Server and PostgreSQL are tested. MIT.

## Three-line setup

```csharp
builder.Services.AddQueryShape();                                                        // 1
builder.Services.AddDbContext<ShopDbContext>(o => o.UseSqlite(conn).UseQueryShape());    // 2
app.UseQueryShape();                                                                     // 3  one analysis scope per request
```

Outside ASP.NET Core, open a scope yourself:

```csharp
using var scope = QueryShapeScope.Begin();
await service.GetOrdersAsync(customerId: 42);
foreach (var d in scope.Analyze()) Console.WriteLine(DiagnosisFormatter.Format(d));
```

For OpenTelemetry add `builder.Services.AddQueryShapeOpenTelemetry();` (and `.AddSource("QueryShape")` / `.AddMeter("QueryShape")` to your providers if you want QueryShape's own activities or metrics exported).

## Snapshot tests (`QueryShape.Testing`)

```csharp
[Fact]
public async Task GetOrders_query_shape()
{
    using var scope = QueryShapeScope.Begin();
    await _sut.GetOrdersAsync(customerId: 42);
    await scope.MatchSnapshotAsync();     // __querysnapshots__/OrderServiceTests.GetOrders_query_shape.json
}

[Fact, QueryBudget(MaxQueries = 2, MaxDurationMs = 200, FailOn = Severity.Error)]   // QueryShape.Testing.Xunit
public async Task GetOrders_stays_within_budget() { ... }
```

First run writes the snapshot and passes (in CI, `CI=true`, a missing snapshot fails). Later runs compare the **multiset of query fingerprints**, never timings or parameter values. Update with `QUERYSHAPE_UPDATE_SNAPSHOTS=1`.

When someone introduces an N+1, the test fails like this:

```
QueryShape snapshot mismatch: OrderServiceTests.GetOrders_query_shape
  snapshot: tests/Shop.Tests/__querysnapshots__/OrderServiceTests.GetOrders_query_shape.json

Queries: 2 in snapshot, 12 now (+10)
  + x10  a2d461845296  Linq  SELECT "t0"."Id", "t0"."OrderId", "t0"."ProductId", "t0"."Quantity" FROM "OrderLines" AS "t0" WHERE "t0"."ProductId" = @p0
        at OrderService.cs:42 OrderService.GetOrdersAsync
        linq: DbSet<OrderLine>() .Where(l => l.ProductId == @p_Id)

New diagnostics (severity >= Warning):
  QS001 ERROR  N+1 query: OrderLine by ProductId executed 10 times at OrderService.cs:42 OrderService.GetOrdersAsync
    why  EF Core translates each LINQ query into exactly one SQL statement at the moment it is enumerated, and
         it cannot see the loop around it. This query loads the OrderLine rows for one Product at a time
         (filtering on OrderLine.ProductId); it ran 10 times in this scope with 10 different parameter values, ...
    fix  Add .Include(p => p.Lines) to the Product query at OrderService.cs:40 OrderService.GetOrdersAsync
         before: DbSet<Product>()
         after:  DbSet<Product>()
                     .Include(p => p.Lines)
         patch:
           --- a/src/Shop/OrderService.cs
           +++ b/src/Shop/OrderService.cs
           -        var products = await db.Products.ToListAsync();
           +        var products = await db.Products.Include(p => p.Lines).ToListAsync();
    data 10 queries, 1.2 ms, 30 rows, distinctParameterSets=10, threshold=5
    docs https://github.com/queryshape/QueryShape/blob/main/docs/rules/QS001.md

If this change is intended, update the snapshot: QUERYSHAPE_UPDATE_SNAPSHOTS=1 dotnet test, or `dotnet queryshape snapshots update`.
```

## Rules

| Id | Name | Severity | Status |
|----|------|----------|--------|
| [QS001](docs/rules/QS001.md) | N+1 query | Error | ✅ |
| [QS002](docs/rules/QS002.md) | Cartesian explosion | Error | ✅ |
| [QS003](docs/rules/QS003.md) | Client-side evaluation | Error | ✅ |
| [QS004](docs/rules/QS004.md) | Unbounded result set | Warning | ✅ |
| [QS005](docs/rules/QS005.md) | Tracking on read-only query | Info | ✅ |
| [QS006](docs/rules/QS006.md) | Missing split query candidate | Warning | ✅ |
| [QS007](docs/rules/QS007.md) | `Contains` on large collection | Warning | ✅ |
| [QS008](docs/rules/QS008.md) | Duplicate identical query | Warning | ✅ |
| [QS009](docs/rules/QS009.md) | Query in loop over navigation | Warning | ✅ |
| [QS010](docs/rules/QS010.md) | Raw SQL with string concatenation | Error | ✅ |

Every rule doc explains what EF Core does and why, and shows the fix. Architecture decisions live in [docs/adr](docs/adr).

## Sample app

`tests/QueryShape.SampleApp` has one deliberately bad endpoint per rule (`/bad/n-plus-one`, `/bad/unbounded`, …) and a fixed twin under `/good/`. Run it and look at the `X-QueryShape` response header:

```
dotnet run --project tests/QueryShape.SampleApp
curl -sD - -o /dev/null http://localhost:5000/bad/n-plus-one | grep X-QueryShape
# X-QueryShape: 41 queries; QS001 Error, QS004 Warning
```

## Safety

QueryShape runs inside your production app, so it never throws out of an interceptor, keeps only bounded state (10 000 commands per scope, then `QS_OVERFLOW`), never records parameter values unless `IncludeParameterValues` is switched on, and never walks stack traces unless `CaptureCallSites` is on (tests turn it on; production leaves it off and can use EF Core's `TagWithCallSite()` instead).

## Building

```
dotnet build QueryShape.slnx
dotnet test QueryShape.slnx            # locally: .NET 10 SDK only, net8.0 tests roll forward
```

CI runs every target on its real runtime (Linux and Windows). Provider tests (SQL Server, PostgreSQL) use Testcontainers and are skipped when Docker is unavailable.
