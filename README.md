<p align="center">
  <img src="https://raw.githubusercontent.com/DanilAntyp/QueryShape/main/docs/assets/queryshape-hero.svg" alt="QueryShape — Your tests pass. Do your queries scale? EF Core query contracts, behavior checks, and failure reduction." width="100%">
</p>

<p align="center">
  <a href="https://github.com/DanilAntyp/QueryShape/actions/workflows/ci.yml"><img src="https://github.com/DanilAntyp/QueryShape/actions/workflows/ci.yml/badge.svg" alt="Build and tests"></a>
  <a href="https://github.com/DanilAntyp/QueryShape/actions/workflows/real-world.yml"><img src="https://github.com/DanilAntyp/QueryShape/actions/workflows/real-world.yml/badge.svg" alt="Real application validation"></a>
  <img src="https://img.shields.io/badge/.NET-8%20%7C%2010-8B7CFF" alt=".NET 8 and 10">
  <img src="https://img.shields.io/badge/EF%20Core-8%20%7C%2010-64DFC7" alt="EF Core 8 and 10">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-64DFC7" alt="MIT license"></a>
  <img src="https://img.shields.io/badge/status-preview-F3C969" alt="Preview software">
</p>

<p align="center">
  <strong>Catch EF Core query regressions before they reach production.</strong><br>
  Run your real operations. Check how queries grow. Validate what an optimization preserves.
</p>

<p align="center">
  <a href="#try-it-now"><strong>Try the demo</strong></a> ·
  <a href="#tested-on-real-applications">Real application results</a> ·
  <a href="#use-it-in-your-tests">Get started</a> ·
  <a href="docs/scenarios.md">Scenario guide</a> ·
  <a href="docs/ci.md">CI integration</a>
</p>

## Why QueryShape?

A test can return the right JSON while making one database call per customer. A four-item page can fetch every brand in the database. A query rewrite can reduce commands and accidentally drop records.

**QueryShape makes those risks testable.** It captures the SQL your EF Core operation actually executes, links findings to source locations when available, and gives you contracts you can review in a pull request.

| Developer question | What you can check |
|---|---|
| Did this change add database calls? | Commit query snapshots; fail CI when query shapes or counts change. |
| What happens with 1,000 customers? | Run the same scenario at different sizes and enforce command/returned-row budgets. |
| Did my optimization change the result? | Compare explicit result and database-state observations in isolated fixtures. |
| Does this patch actually help? | Repeat before/after tests in a Git worktree and check separate metric budgets. |
| Why does this fail only on a big fixture? | Reduce a reproducible failure to smaller input and export a replay test. |

**Runs locally. No hosted account. No API key needed for detection, contracts, or verification.** Works with xUnit, NUnit and MSTest; optional ASP.NET Core and OpenTelemetry integrations.

## Try it now

You need **Git and the .NET 10 SDK**. The demo uses in-memory SQLite; there is no database server to configure. The first run restores dependencies from NuGet.

```sh
git clone https://github.com/DanilAntyp/QueryShape.git
cd QueryShape
dotnet run --project src/QueryShape.Cli -f net10.0 -- scale --project tests/QueryShape.Testing.Tests --test FullyQualifiedName~ScenarioExamples.Scale_customer_orders --sizes 10,100,1000
```

This deliberately inefficient fixture loads customers, then counts orders once per customer. The measured counts are:

```text
Customers        SQL commands        Rows returned
       10                  11                   20
      100                 101                  200
    1,000               1,001                2,000

Constant-command contract: FAIL
```

**Exit code 1 is the expected result:** QueryShape caught the growth. The CLI prints repeated measurements and source evidence; the multi-target test project runs against both EF Core versions. This is an executable synthetic example, separate from the external application trials below.

See the [complete fixture](tests/QueryShape.Testing.Tests/ScenarioTests.cs) and its batched version. To run the regression test that checks both:

```sh
dotnet test tests/QueryShape.Testing.Tests -f net10.0 --filter FullyQualifiedName~Scaling_detects_n_plus_one_and_confirms_the_batched_fix
```

Prefer HTTP? Run the [sample application](tests/QueryShape.SampleApp) and compare `/bad/n-plus-one` with `/good/n-plus-one`. Its response headers report **41 queries versus 1** for the seeded sample:

```sh
dotnet run --project tests/QueryShape.SampleApp -f net10.0 --urls http://localhost:5077
# In a second terminal:
curl -i http://localhost:5077/bad/n-plus-one
curl -i http://localhost:5077/good/n-plus-one
```

## Tested on real applications

Actual upstream service code. Pinned revisions. Reproducible harnesses. Synthetic datasets, with the adaptations and measurement limits documented.

**[Hosted validation: all 17 application tests passed →](https://github.com/DanilAntyp/QueryShape/actions/runs/34278103529)** Scope reports and test results are attached to the run.

<p align="center">
  <img src="https://raw.githubusercontent.com/DanilAntyp/QueryShape/main/docs/assets/real-world-results.svg" alt="eShopOnWeb catalog lookup trial: at sizes 10, 100, and 501, commands stayed at 4 while returned rows increased from 25 to 205 to 1,007." width="100%">
</p>

### Microsoft eShopOnWeb

**15 harness tests passed**, exercising catalog, basket, checkout, order history, scaling, behavior comparison and reduction.

| Actual operation | What QueryShape recorded | Why it matters |
|---|---|---|
| Catalog with growing brand/type lists | **4 queries at every size; 25 → 205 → 1,007 returned rows** | Query count alone misses the growing data transfer. |
| Catalog pagination | **QS011:** `Skip`/`Take` without an explicit `OrderBy` | Flags a query shape that can produce unstable pages. |
| Catalog with tracking disabled in the harness | Recorded result/state matched; tracking findings fell **3 → 0** | Checks the observed behavior of a candidate change. |
| Large basket lookup | **QS007** reproduced with 501 product IDs | Reduced a 1,000-product fixture to **501 in 42 trials**. |

Complete dropdowns may be intentional. The 501-ID boundary is a configured diagnostic threshold, not a measured performance cliff. The catalog cache and production latency were outside this trial.

[Read the case study and measurements →](docs/validation/eshoponweb-scenarios.md) · [Reproduce the run →](scripts/real-world/eShopOnWeb/README.md)

### Ardalis CleanArchitecture

The original contributor-list service used ordered pagination and projection over raw SQL. With **0, 1, 10 and 100 contributors**, QueryShape recorded **2 commands** and **1, 2, 5 and 5 returned rows** respectively, with **no rule diagnoses** in the exercised paths.

A deliberately incorrect candidate dropped contributors without phone numbers. QueryShape detected the result mismatch and reduced **12 contributors to one in 19 trials**. This was an introduced regression for validation, not a bug claim against the upstream project. **Both harness tests passed.**

[Read the case study and machine-readable evidence →](docs/validation/cleanarchitecture.md) · [Reproduce the run →](scripts/real-world/CleanArchitecture/README.md)

Both application trials used SQLite in memory. They establish results for those fixtures, not production speed or a repository-wide clean bill of health. [Full validation record and remaining gaps](docs/validation/README.md).

## Use it in your tests

This is a **source preview**. You can run the CLI from this checkout today; [installation instructions](docs/installation.md) cover building local packages and adding the library to your project without relying on a public NuGet release.

Register capture on the context your integration test actually uses:

```csharp
using QueryShape;

optionsBuilder.UseSqlite(connection).UseQueryShape();
```

Wrap one real operation after fixture setup and seeding:

```csharp
using QueryShape;
using QueryShape.Testing;

[Fact]
public async Task GetOrders_query_contract()
{
    using var scope = QueryShapeScope.Begin("GetOrders");
    var result = await service.GetOrdersAsync(customerId: 42);

    Assert.NotEmpty(scope.Commands); // Confirm this context is instrumented.
    scope.Observe("result", result); // Materialized result DTOs.
    await scope.MatchSnapshotAsync();
}
```

The first local run creates a snapshot. Review and commit it. Later runs compare query fingerprints and counts; timings and parameter values are excluded. Missing snapshots fail in CI. Update intentional changes with `QUERYSHAPE_UPDATE_SNAPSHOTS=1`.

After installing the CLI:

```sh
dotnet queryshape doctor --project tests/Shop.Tests --test GetOrders_query_contract
dotnet queryshape report --project tests/Shop.Tests --test GetOrders_query_contract --fail-on warning
```

`doctor` confirms tests, scopes, captured commands and completeness. `init --out QueryShapeSmokeTests.cs --framework xunit` generates a wrapper for you to connect to your fixture. For `WebApplicationFactory`, set `factory.Server.PreserveExecutionContext = true`.

[First-run troubleshooting →](docs/getting-started.md)

## Go beyond snapshots

**Enforce growth budgets.** Prepare an isolated fixture for each input, then express the cost your operation should have:

```csharp
var report = await QueryScaling.RunAsync(scenario, [0, 10, 100, 1000],
    new ScalingOptions
    {
        ConstantCommands = false,
        MaxCommandsForSize = n => 1 + (n + 99) / 100,
        MaxRowsForSize = n => 2L * n
    });
report.AssertSatisfied();
```

Named cases cover empty data, batch boundaries and skewed relationships. [Build a scenario →](docs/scenarios.md)

**Validate a patch against explicit contracts.**

```sh
dotnet queryshape verify --project tests/Shop.Tests --test GetOrders_behavior --patch optimization.diff --max-commands 3 --max-rows 200
```

The CLI repeats baseline and candidate runs in an isolated worktree. It checks passing test identities, scope coverage, complete capture, recorded behavior, new finding identities and separate command/row/duration budgets. Explicit budgets allow deliberate tradeoffs such as split queries. Keep the selected functional test separate from snapshots that intentionally change with the patch.

**Keep intentional findings visible.** Accept a specific finding with a reason, expiry and occurrence limit; new or increased findings still gate CI:

```sh
dotnet queryshape baseline --report-dir artifacts/queryshape --id <finding-id> --reason "Complete dropdown required by the UI" --expires 2026-12-31 --out queryshape-acceptances.json
dotnet queryshape report --project tests/Shop.Tests --fail-on warning --acceptances queryshape-acceptances.json
```

[CI, exit codes and acceptance policy →](docs/ci.md)

## What it detects and what it measures

| Area | Rules / capabilities |
|---|---|
| Repeated work | N+1, duplicate queries, repeated navigation-query call sites |
| Data growth | Unbounded results, Cartesian explosion, split-query candidates, large `Contains` inputs |
| Query correctness risks | Pagination without ordering, client-side evaluation patterns, raw SQL concatenation risks |
| Tracking | Tracked reads with no save observed in the measured scope |
| Regression contracts | Query snapshots, command and returned-row budgets, explicit result/state comparison |

[Browse every rule →](docs/rules)

Findings distinguish observed patterns from heuristic risks. **Returned rows are not rows scanned. Command duration is not endpoint latency.** Behavior checks cover only the result/state projections you record. Pair QueryShape with your database's execution plans and production telemetry when investigating server cost.

Capture is bounded and configurable; SQL parameter values are excluded by default. Call-site capture and source reads add overhead. Optional `explain --llm` / `fix --llm` workflows send selected source only when explicitly enabled. [Performance measurements and limits](docs/performance.md).

## Build, contribute, explore

```sh
dotnet build QueryShape.slnx
dotnet test QueryShape.slnx --filter 'Category!=Slow'
dotnet test tests/QueryShape.Cli.Tests --filter 'Category=Slow'
```

The solution requires the .NET 10 SDK. Libraries and CLI target .NET 8 / EF Core 8 and .NET 10 / EF Core 10. Docker enables SQL Server/PostgreSQL integration tests. CI is configured for Linux and Windows with both runtimes.

The recorded local audit passed **459 repository tests and 17 external-harness tests**, with **18 repository tests skipped**. Native runtime/provider validation is tracked separately; see the [audit record](docs/validation/adoption-audit.md) and the live CI badge for current results.

- [Contributing](CONTRIBUTING.md) — run checks, report findings, add a real application case.
- [Architecture decisions](docs/adr) — why the capture and verification APIs work this way.
- [Validation evidence](docs/validation) — inspect measurements, limitations and replay inputs.
- [Open an issue](https://github.com/DanilAntyp/QueryShape/issues/new/choose) — bring a query, a reproduction, or a first-run problem.

**Start with one important operation. Give its queries a regression test.**
