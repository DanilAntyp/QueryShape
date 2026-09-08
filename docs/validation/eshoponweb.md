# eShopOnWeb field test — 2026-09-08

QueryShape was run against [dotnet-architecture/eShopOnWeb](https://github.com/dotnet-architecture/eShopOnWeb/tree/4da8212117e87d808d4bbc7da6286fd2147ce606), revision `4da8212117e87d808d4bbc7da6286fd2147ce606`. Seven scenarios passed, each executed twice in a fresh context/scope: **14 scopes, 40 database commands, 10 warnings, 30 informational diagnoses, no Error diagnoses**. Counts include both executions and the two deliberately enlarged datasets; they are not counts of unique defects.

The [reproducible harness](../../scripts/real-world/eShopOnWeb/README.md) references the original repositories, domain services and specifications, and compiles the original catalog/basket view-model services. The upstream checkout remained unchanged. The only provider adaptation is replacing SQL Server HiLo keys with SQLite identity generation in a derived context. This run used EF Core 8.0.30 on .NET 10 through net8.0 runtime roll-forward, with synthetic data. SQL Server plans, HTTP endpoints and the application's catalog memory-cache wrapper were not tested.

## What QueryShape found

| Finding | Location in upstream source | Evidence and interpretation |
|---|---|---|
| **QS011 Warning: pagination without ordering** | `CatalogFilterPaginatedSpecification.cs:20–22`; executed at `CatalogViewModelService.cs:49` | The catalog query uses Skip/Take without OrderBy. Both catalog scenarios report EF's own warning on both executions. Add a stable order, such as `Query.OrderBy(i => i.Id)`, before Skip/Take in the specification. |
| **QS005 Info: unnecessary change tracking** | Catalog service lines 49, 83, 99; basket service lines 31, 56; order service lines 33, 39; `CustomerOrdersWithItemsSpecification` | Products, brands, types, baskets and order history are materialized with tracking despite no changes being saved for those types in the measured scope. Use no-tracking read specifications or project to view models in the database. Order creation writes new orders but does not modify the basket/catalog entities it read. Review scope boundaries before changing tracking. |
| **QS004 Info/Warning: full lookup-table reads** | `CatalogViewModelService.GetBrands:83` and `GetTypes:99` | All 6 brands and 5 types are read in the ordinary dataset: Info. The same code reads all 30 brands and 25 types in the enlarged dataset: Warning. These are intentionally complete dropdown lists, so the warning is a growth/caching consideration, not proof that paging the lists is appropriate. The production cache wrapper was outside this test. |
| **QS007 Warning: large collection filter** | `BasketViewModelService.GetBasketItems:56`, using `CatalogItemsSpecification` | The stress basket sends 501 product IDs in a JSON collection parameter, unpacked by SQLite's `json_each`; threshold is 500. Ordinary 3-item baskets do not trigger this. Consider a database join/subquery or an explicit basket-size limit if such baskets are supported. |

The database-side basket item count executed one aggregate command and produced no diagnoses. Basket and checkout catalog lookups were batched; no N+1, duplicate-query or Cartesian-explosion diagnosis appeared in these scenarios. This is coverage of the exercised paths, not a clean bill of health for the entire application. NuGet separately reported advisories in the archived application's System.Text.Json dependency; those are not QueryShape findings.

## Per-scenario results

Counts below are **per execution**; each scenario ran twice with the same query counts and diagnostic categories.

| Scenario | Commands | Warnings | Info | Rules |
|---|---:|---:|---:|---|
| Catalog, ordinary lookups | 4 | 1 | 5 | QS011, QS004, QS005 |
| Catalog, 30 brands / 25 types | 4 | 3 | 3 | QS011, QS004, QS005 |
| Basket, 3 products | 2 | 0 | 2 | QS005 |
| Basket, 501 products | 2 | 1 | 2 | QS007, QS005 |
| Basket item count | 1 | 0 | 0 | none |
| Checkout | 6 | 0 | 2 | QS005 |
| Order-history specification | 1 | 0 | 1 | QS005 |

Checkout's six commands comprise two reads and four INSERT commands. Setup/schema/seed commands were excluded. SQLite in-memory timings are unsuitable for claims about production latency.

## QueryShape problems fixed during the test

1. **Lost query metadata when an optional filter disappears.** EF simplifies unset brand/type filters to true and removes WHERE. QueryShape previously rejected this SQL/expression match, silently missing both unordered pagination and product tracking. It now recognizes constant-true predicates without evaluating user code. Both cold and cached executions retain their diagnoses; regression tests cover EF Core 8 and 10. See [ADR-0010](../adr/0010-optional-filters-removed-by-ef.md).
2. **Incorrect root-entity counts on collection includes.** A basket with 501 items was described as “501 Basket entities.” QS005 now omits the numeric root count when collection includes multiply rows. Row counts remain in the evidence.
3. **Hidden failures and misleading CLI success.** The first harness run failed because SQLite cannot create SQL Server sequences. `report` hid that exception. It now prints the underlying test output on stderr, exits with 2 when tests fail even if some scope reports exist, and identifies those as partial results. `explain` also rejects failed/empty runs. Empty or nonexistent report directories cannot be mistaken for clean runs. Eight CLI regression cases cover these behaviors, including valid JSON stdout for partial reports.

## Evidence and validation

[Captured reports](eshoponweb-reports.json) contain the 14 final scope reports as a JSON array, sorted by scope name. This is the same array format as `report --json`; individual reports generated by the harness can be read using `report --report-dir`.

The initial all-at-once solution run exposed a build collision between solution compilation and the CLI tests' nested sample-app builds. For validation, the ordinary suite and the worktree tests were run separately:

```sh
dotnet test QueryShape.slnx --no-restore --filter 'Category!=Slow'
dotnet test tests/QueryShape.Cli.Tests --no-build --no-restore --filter 'Category=Slow'
```

The ordinary suite passed **408 tests**, with **17 skips** for unavailable Docker providers and ASP.NET Core 8 test-host cases under runtime roll-forward. All **four CLI worktree tests** passed separately, for **412 passing QueryShape tests** overall. The eShopOnWeb harness passed all seven scenarios, including its final run through the improved CLI. The two new core regression cases first failed on both EF Core targets and passed after the fixes.
