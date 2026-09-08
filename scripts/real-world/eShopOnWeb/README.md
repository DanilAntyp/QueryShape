# eShopOnWeb validation harness

Runs original eShopOnWeb services and specifications against QueryShape using an in-memory SQLite database. This project is opt-in and is not part of the regular solution; it requires a separate upstream checkout.

From the QueryShape repository root:

```sh
git clone https://github.com/dotnet-architecture/eShopOnWeb.git /tmp/queryshape-eshoponweb
git -C /tmp/queryshape-eshoponweb checkout 4da8212117e87d808d4bbc7da6286fd2147ce606
export EShopRoot=/tmp/queryshape-eshoponweb
export QUERYSHAPE_REPORT_DIR=/tmp/queryshape-eshop-reports
dotnet test scripts/real-world/eShopOnWeb/eShopOnWeb.Tests.csproj
dotnet run --project src/QueryShape.Cli -f net10.0 -- report --report-dir "$QUERYSHAPE_REPORT_DIR"
```

Use a fresh report directory per run to avoid accumulating duplicate reports. `EShopRoot` can point to any checkout location; use the pinned revision above for reproducible results. Run commands from QueryShape so the archived application's SDK pin does not select an unavailable SDK.

The harness references the upstream Infrastructure/ApplicationCore projects and links the original catalog/basket service source files and view models. It does not rewrite their queries. Its derived context replaces SQL Server HiLo sequence generation with SQLite identity generation; all other mappings remain upstream. EF Core resolves to 8.0.30 through the harness, and net8.0 tests roll forward when only a newer runtime is installed. The archived upstream packages can produce NuGet advisory warnings; those are separate from QueryShape findings.

Seven scenarios each execute twice in a fresh context/scope, covering cached query compilation. Seed data is synthetic: 12 products, 6 brands, 5 types, one basket with 3 lines, and one order. Two stress variants use 30 brands/25 types and a basket with 501 distinct products. Schema creation and seeding occur outside measured scopes. This tests service/specification behavior, not HTTP endpoints, the catalog memory cache, or SQL Server execution plans.

See [the findings and recorded evidence](../../../docs/validation/eshoponweb.md).

`ScenarioTests` additionally exercises scaling at 10/100/501 items, catalog behavior equivalence with tracking disabled, and reduction of the large-basket QS007 warning from 1,000 to 501 products. The lookup-list scenario intentionally asserts that a constant-row contract fails; its xUnit test passes when that violation is detected, while `queryshape scale` exits 1. See [scenario measurements and CLI reproduction commands](../../../docs/validation/eshoponweb-scenarios.md).
