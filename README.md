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
    await scope.MatchSnapshotAsync();     // __querysnapshots__/OrderServiceTests.GetOrders_query_shape.sqlite.json
}

[Fact, QueryBudget(MaxQueries = 2, MaxDurationMs = 200, FailOn = Severity.Error)]   // QueryShape.Testing.Xunit
public async Task GetOrders_stays_within_budget() { ... }
```

First run writes the snapshot and passes (in CI, `CI=true`, a missing snapshot fails). Later runs compare the **multiset of query fingerprints**, never timings or parameter values. Update with `QUERYSHAPE_UPDATE_SNAPSHOTS=1`. The provider is part of the file name, so a suite that runs on SQLite locally and SQL Server in CI keeps one snapshot per provider instead of failing on SQL dialect differences.

Call sites (`at OrderService.cs:42`) and source patches come from stack walks and source reads. Both are on by default when a test framework (xUnit, NUnit, MSTest, TUnit) is loaded in the process and off otherwise; `QueryShapeOptions.CaptureCallSites` and `ReadSourceFiles` override that. With `WebApplicationFactory`, set `factory.Server.PreserveExecutionContext = true` so the test's scope encloses the request scope the middleware opens (TestServer drops `AsyncLocal` values otherwise). For `[Theory]` rows or a shared helper, pass `name:` (and `callerFilePath:`/`callerMemberName:`) to `MatchSnapshotAsync` so each case gets its own file.

When someone introduces an N+1, the test fails like this:

```
QueryShape snapshot mismatch: OrderServiceTests.GetOrders_query_shape
  snapshot: tests/Shop.Tests/__querysnapshots__/OrderServiceTests.GetOrders_query_shape.sqlite.json

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

## Prove the fix: `dotnet queryshape verify`

The CLI runs the named test before and after a patch (applied in a clean git worktree, never your working copy), measures both with QueryShape, and prints the proof. Exit code 0 means improved with no new Error-level diagnosis.

```
dotnet queryshape verify --project tests/QueryShape.SampleApp.Tests \
    --test BadEndpointTests.N_plus_one_is_diagnosed_with_include_fix --patch n-plus-one-fix.diff
```

```
Fix: n-plus-one-fix.diff

                        before     after         Δ
queries                     41         1       -40
duration (ms)              1.4       0.9      -32%
QS001 N+1 query              1         0         ✓
QS004 Unbounded query        1         1         =
QS005 Tracked read-on…       2         1        -1
new diagnostics              -         0         ✓

after = median of 3 runs (spread 0.2 ms)
note: the duration delta is within run-to-run noise; judge by queries and diagnostics

verdict: improved
```

`--patch-from-diagnosis` uses the patch QueryShape itself proposed in the baseline run; when that patch is only part of the fix (for example the loop still has to read the navigation), verify says so and refuses to print a misleading table. `--format json|markdown` gives CI something to post; see [docs/ci.md](docs/ci.md) for the GitHub Action that comments the report or the verify table on every pull request. Other commands: `queryshape report` (print every diagnosis of a test run), `queryshape snapshots update`, and `queryshape explain [--llm --show-prompt]`. The `--llm` path is off by default, needs `ANTHROPIC_API_KEY`, sends only the diagnosis JSON and the enclosing source method (printed verbatim with `--show-prompt`), and labels its output as generated; detection never depends on it.

`dotnet queryshape fix --llm --test <name>` closes the loop: the model gets the diagnosis and the enclosing method, answers with a unified diff (or `CANNOT`), and that diff goes through the same worktree-and-measure flow as `verify`. The model is never trusted: a patch that does not apply, touches files outside the repository, or fails to improve the numbers is rejected, and the output is labeled as model-generated with the table as the proof.

Tests report to the CLI through `QUERYSHAPE_REPORT_DIR`: when that variable is set, every completed scope writes a JSON summary (shapes, counts, timings, diagnoses; never parameter values).

## Packages

| Package | What |
|---|---|
| `QueryShape.Core` | capture, normalization, rules, diagnoses |
| `QueryShape.Testing` | snapshot testing and `QueryBudget` (framework-agnostic) |
| `QueryShape.Testing.Xunit` / `.NUnit` / `.MSTest` | `[QueryBudget]` attributes for each framework |
| `QueryShape.AspNetCore` | `app.UseQueryShape()` request scopes |
| `QueryShape.OpenTelemetry` | span enrichment and metrics |
| `QueryShape.Cli` | `dotnet queryshape` tool |
| `QueryShape.Analyzers` | Roslyn analyzer for the mistakes the runtime cannot see (QSA001) |

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
| [QS011](docs/rules/QS011.md) | Row limiting without OrderBy (from EF Core's own warning) | Warning | ✅ |
| [QSA001](docs/rules/QSA001.md) | Where/First/OrderBy/Take… right after `ToList()` on a query (compile-time, `QueryShape.Analyzers`) | Warning | ✅ |
| [QS_OVERFLOW](docs/rules/QS_OVERFLOW.md) | A scope hit `MaxCommandsPerScope` and stopped recording | Warning | ✅ |

Every rule doc explains what EF Core does and why, and shows the fix. Architecture decisions live in [docs/adr](docs/adr).

## Sample app

`tests/QueryShape.SampleApp` has one deliberately bad endpoint per rule (`/bad/n-plus-one`, `/bad/unbounded`, …) and a fixed twin under `/good/`. Run it and look at the `X-QueryShape` response header:

```
dotnet run --project tests/QueryShape.SampleApp -f net10.0 --urls http://localhost:5000
curl -sD - -o /dev/null http://localhost:5000/bad/n-plus-one | grep X-QueryShape
# X-QueryShape: 41 queries; QS001 Error, QS004 Warning, QS005 Info, QS005 Info
```

The sample app targets both .NET 8 and .NET 10, so `dotnet run` needs `-f`.

## Safety and performance

QueryShape runs inside your production app, so it never throws out of an interceptor, keeps only bounded state (10 000 commands per scope, then [`QS_OVERFLOW`](docs/rules/QS_OVERFLOW.md) with the number of commands it dropped), never records parameter values unless `IncludeParameterValues` is switched on, masks literals in raw SQL shapes, never walks stack traces or reads source files unless `CaptureCallSites`/`ReadSourceFiles` are on (on by default only when a test framework is loaded in the process; production can use EF Core's `TagWithCallSite()` instead, or `CallSiteSamplingInterval = 100` to locate each query shape on its first execution and then one in a hundred), and has a kill switch (`QueryShapeOptions.Enabled = false`).

Capture costs about 3.5 µs and 1.6 KB per query with call-site capture off (BenchmarkDotNet, see [docs/performance.md](docs/performance.md)): under 3 % of a query against any networked database, but a visible fraction of an in-memory SQLite query.

## Building

```
dotnet build QueryShape.slnx
dotnet test QueryShape.slnx            # locally: .NET 10 SDK only, net8.0 tests roll forward
```

CI runs every target on its real runtime (Linux and Windows). Provider tests (SQL Server, PostgreSQL) use Testcontainers and are skipped when Docker is unavailable.
