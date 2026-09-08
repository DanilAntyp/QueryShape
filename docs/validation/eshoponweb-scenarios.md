# eShopOnWeb: scaling, behavior, and reduction

Executed on 2026-09-08 against [Microsoft eShopOnWeb](https://github.com/dotnet-architecture/eShopOnWeb/tree/4da8212117e87d808d4bbc7da6286fd2147ce606), commit `4da8212117e87d808d4bbc7da6286fd2147ce606`. The upstream checkout remained unchanged. The harness calls its actual catalog and basket services, repositories, and specifications.

**15 validation tests passed:** seven existing service scenarios and eight new scaling/behavior/reduction cases. Four scaling scenarios each ran twice at sizes 10, 100, and 501, giving 24 measurements. The scaling CLI correctly exited **1** because the strict constant-row contract for lookup lists failed. The test asserting detection of that violation passed. Reduction exited **0**.

## Measurements

Each cell is **SQL commands / rows returned** per service invocation. Both repetitions agreed. Rows returned measure data transferred through database readers, not rows scanned inside the database or execution-plan cost.

| Scenario | Size 10 | Size 100 | Size 501 | Contract result |
|---|---:|---:|---:|---|
| Catalog, growing product table, page size 4 | 4 / 16 | 4 / 16 | 4 / 16 | Pass: constant commands and returned rows |
| Catalog, growing brand and type tables | 4 / 25 | 4 / 205 | 4 / 1,007 | Fail: returned rows grow |
| Basket, growing distinct product count | 2 / 20 | 2 / 200 | 2 / 1,002 | Pass: constant commands, at most two returned rows per product |
| Basket quantity total | 1 / 1 | 1 / 1 | 1 / 1 | Pass: constant commands and returned rows |

The lookup scenario has N brands and N types, with 12 fixed products. The product-growth scenario has six fixed brands and five fixed types. Every basket line has quantity two.

## What QueryShape found

- **QS011 — pagination without an explicit order.** `CatalogFilterPaginatedSpecification` applies `Skip` and `Take` without `OrderBy`; the caller is `CatalogViewModelService.cs:49`. QueryShape detected the unsafe query shape. These fixtures did not demonstrate an actual page duplication or omission.
- **QS004 — complete brand/type table reads.** `CatalogViewModelService.cs:83` and `:99` retrieve all dropdown entries. Returned rows grow as `2N + 5`, despite the four-product page. Complete dropdowns may be intentional; changing them requires a UI decision. The production catalog cache was outside this measurement.
- **QS005 — tracking on reads used to produce view models.** The catalog produces three informational diagnostics per execution. Basket and other original scenarios also produce read-tracking diagnostics.
- **QS007 — a large product-ID collection in basket loading.** The basket service gathers product IDs and queries them together. The warning begins above the configured default threshold of 500 IDs.

No N+1 SQL growth was observed in the measured catalog/basket operations. There were no Error-severity diagnoses or overflowing scopes in the exported run. Constant returned rows do not establish constant database work: for example, the catalog still executes a count query.

## Behavior check

`QueryEquivalence.CompareAsync` compared the original catalog operation with the same service using a fresh context configured with `QueryTrackingBehavior.NoTracking`. At each of 10, 100, and 501 products:

- The full serialized catalog view model matched, preserving list order.
- The observed product-state projection matched before and after, and remained unchanged.
- SQL stayed at four commands and 16 returned rows.
- Separate captured runs confirmed that QS005 diagnostics fell from three to zero.

This validates the recorded result and product-state projection for these fixtures. It does not establish application-wide equivalence or measure a latency improvement. The change was exercised in the harness; no upstream production code was modified.

## Reduced reproducer

The reducer started with a basket containing **1,000 distinct products**, retained the **QS007** signature, and reduced it to **501** in **42 trials**, with two confirmations for retained failures. A separate 500-product check did not trigger QS007. The size generator supplies progressively smaller basket sizes; the reducer reached a local minimum among those candidates. The result also matches the rule's explicit threshold.

This is a minimized diagnostic reproducer, not evidence of a performance cliff at 501 products. The generated [xUnit test](eshoponweb-reproducer/QueryShapeRegression_300d75c053c8447db5e6513f58a10a13.cs) embeds the [input](eshoponweb-reproducer/QueryShapeRegression_300d75c053c8447db5e6513f58a10a13.json) and calls `ScenarioTests.BasketWarningStillReproducesAsync`. To run it, copy it into the opt-in harness and select its generated class name with `dotnet test --filter`. It is expected to fail while the warning still reproduces. The reduction and replay helper were executed; this exported generated class was not separately compiled in this run.

## Reproduce

First follow the checkout instructions in the [harness README](../../scripts/real-world/eShopOnWeb/README.md). Then, from the QueryShape root:

```sh
dotnet run --project src/QueryShape.Cli -f net10.0 -- scale \
  --project scripts/real-world/eShopOnWeb/eShopOnWeb.Tests.csproj \
  --test 'FullyQualifiedName~ScenarioTests|FullyQualifiedName~ShopTests' \
  --sizes 10,100,501 --json
# Expected exit: 1 (the lookup constant-row contract fails).

dotnet run --project src/QueryShape.Cli -f net10.0 -- reduce \
  --project scripts/real-world/eShopOnWeb/eShopOnWeb.Tests.csproj \
  --test FullyQualifiedName~ScenarioTests.Reduce_large_basket_warning \
  --out /tmp/queryshape-eshop-reduced --json
# Expected exit: 0; emits a minimized input and regression test.
```

The [machine-readable evidence](eshoponweb-scenarios.json) contains all scaling points, behavior comparisons, tracking counts, reduction results, representative diagnoses with SQL/source locations, and paths to the complete raw reports. Those raw directories are local temporary artifacts.

## Scope and tool result

This run used synthetic data, in-memory SQLite, EF Core 8.0.30, and the net8.0 test target rolled forward to the installed .NET 10 runtime. The harness adapts SQL Server HiLo key generation for SQLite. It does not test HTTP endpoints, the catalog memory cache, SQL Server plans, or production latency. Restore reported advisories for upstream `System.Text.Json` 8.0.3; these were NuGet findings, not QueryShape findings.

No new QueryShape defect was encountered in this run. Changes were limited to adding the reproducible external-repository scenarios, sharing the existing SQLite adapter, referencing the Testing library, and saving evidence.
