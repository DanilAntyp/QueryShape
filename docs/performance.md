# Performance

Measured with BenchmarkDotNet (`tests/QueryShape.Benchmarks`), Release, .NET 10.0.10, Apple M1 Pro, macOS, in-memory SQLite.
Re-run before every release:

```
dotnet run -c Release --project tests/QueryShape.Benchmarks -f net10.0 -- --filter '*CaptureOverheadBenchmarks*' '*NormalizationBenchmarks*' '*RequestBenchmarks*' --join
```

## 0.1.0-preview.1 (2026-09-08), p99 under concurrent load

`dotnet run -c Release --project tests/QueryShape.Benchmarks -f net10.0 -- --load 8 2000`: 8 concurrent workers, 2 000 requests each per endpoint, through the
sample app in-process (TestServer, SQLite file with WAL and pooled connections, request logging off). Each off/on pair runs twice and the second pass is
reported, so JIT and cache warm-up do not favour whichever mode runs second. Same machine as below.

| Endpoint | Mode | p50 (µs) | p95 (µs) | p99 (µs) | p99 ratio |
|---|---|---:|---:|---:|---:|
| `/good/n-plus-one` | off | 607 | 2,119 | 5,419 | 1.00 |
| `/good/n-plus-one` | on | 726 | 2,440 | 5,732 | 1.06 |
| `/bad/n-plus-one` | off | 4,690 | 11,607 | 12,708 | 1.00 |
| `/bad/n-plus-one` | on | 4,779 | 11,612 | 12,666 | 1.00 |

Reading it: the 41-query request is unchanged at p99 (12.7 ms either way; 41 captures plus the eleven rules at scope end cost around 150 µs, hidden inside
the database time), which met the target for this measured workload; it does not establish a general production overhead bound. The one-query request is dominated by
run-to-run noise at p99 (about ±10 % between passes on a 0.6 ms request); its p50 difference, roughly 120 µs, is the fixed cost per request: one capture,
all rules over a one-command scope, and the `X-QueryShape` header. The effect against a networked database has not been established by this SQLite measurement.

## 0.1.0-preview.1 (2026-09-08), after the parameter-hash change

Same machine and method as below. Hashing primitive parameters from their bits instead of formatting them, plus caching the provider name per context:

| Scenario | Mean | vs EF Core | Allocated |
|---|---:|---:|---:|
| EF Core only | 26.43 µs | 1.00 | 17.6 KB |
| QueryShape, no scope | 32.87 µs | 1.24 | 20.9 KB |
| QueryShape, scope per request | 35.16 µs ± 3.4 | 1.33 | 21.7 KB |
| QueryShape, scope + `Analyze()` | 38.44 µs ± 5.1 | 1.45 | 28.5 KB |
| QueryShape, scope + call sites | 65.60 µs | 2.48 | 66.7 KB |

Per query: **≈ 3.5 µs and ≈ 1.6 KB** (was ≈ 4 µs / 2.7 KB). The remaining cost is the `CapturedCommand` and parameter descriptors, the reader wrapper,
the correlator lookups and the scope append; each is small and needed by at least one rule, so this is where the micro work stops for now.

## 0.1.0-preview.1 (2026-09-08), first measurement

### Per-query overhead (micro): one customer lookup + its orders, 2 queries, `AsNoTracking`

| Scenario | Mean | vs EF Core | Allocated |
|---|---:|---:|---:|
| EF Core only | 25.96 µs | 1.00 | 17.6 KB |
| QueryShape, no scope (unscoped ring buffer) | 34.90 µs | 1.34 | 22.7 KB |
| QueryShape, scope per request | 34.25 µs | 1.32 | 23.1 KB |
| QueryShape, scope + `Analyze()` (all 10 rules) | 37.13 µs | 1.43 | 29.1 KB |
| QueryShape, scope + `CaptureCallSites = true` (test mode) | 65.67 µs | 2.53 | 67.9 KB |
| `SqlNormalizer.Normalize` alone (cold, cached afterwards per SQL text) | 8.08 µs | | 19.2 KB |

Capture costs **≈ 4 µs and ≈ 2.7 KB per query** with call-site capture off: `CapturedCommand` + parameter descriptors, the FNV parameter hash,
correlator lookups, the counting reader wrapper, and the scope's list append. Normalization is paid once per distinct SQL text (cache of 2 048 entries).
Running all rules over a 2-query scope adds ≈ 3 µs. Stack walking for call sites adds ≈ 15 µs per query and is for tests only.

### End-to-end request (sample app under `WebApplicationFactory`, `Enabled=false` vs on, response header on)

| Endpoint | QueryShape off | QueryShape on | Ratio |
|---|---:|---:|---:|
| `/good/n-plus-one` (1 query) | 643 µs ± 18 | 728 µs ± 94 | 1.13 ± 0.15 (noise; see below) |
| `/bad/n-plus-one` (41 queries) | 1 454 µs ± 24 | 1 597 µs ± 52 | 1.10 ± 0.04 |

The 41-query request costs 143 µs more, i.e. ≈ 3.5 µs per query, matching the micro numbers; that includes running the rules at response start
for the `X-QueryShape` header. The one-query result is dominated by run-to-run noise (its confidence interval spans the baseline).

### Reading these numbers against the budget

CLAUDE.md asks for **< 3 % p99 latency with call-site capture off**. The relative figure depends entirely on how long a query takes:

| Query round trip | QueryShape overhead per query | Relative |
|---|---:|---:|
| in-memory SQLite (~15 µs), as in the sample app | ≈ 4 µs | ~25 % |
| local PostgreSQL / SQL Server (~150 µs) | ≈ 4 µs | ~2.7 % |
| networked database (≥ 500 µs) | ≈ 4 µs | < 1 % |

The networked-database percentages above are arithmetic illustrations using assumed round-trip times, not provider benchmark results. Real overhead depends on materialization, concurrency, row width, allocations and enabled features. The 3% figure is a target, not a guarantee.

### Call sites in production

`CaptureCallSites` walks the stack on every command (≈ 15 µs each) and is meant for tests. For production, `CallSiteSamplingInterval = N` walks the
stack for the first execution of each query shape and then one in every N executions of that shape; commands in between carry the last sampled
call site marked `Cached`, which rules use but the OpenTelemetry listener does not export (so the `queryshape.callsite` attribute stays at sample rate).
The walk happens synchronously inside the interceptor on the request's own path, sampled or not; there is no background work.

`CallPathDepth` (default 3) decides how many user frames that same walk keeps. The cost of a walk is dominated by building the stack trace with
file information, which resolves symbols for every frame up front; keeping three frames instead of one adds a few comparisons and a small array to
a walk that already happened, and the walk stops as soon as it has the frames it needs. `CallPathDepth = 1` restores the pre-0.1 behaviour of
recording only the call site.

### Providers (2026-09-10, measured)

`ProviderOverheadBenchmarks` on the hosted Ubuntu runner, against disposable PostgreSQL 16 and SQL Server 2022 containers:
one customer lookup plus its orders, two queries, `AsNoTracking`, 3 warmup and 10 measured iterations
([run 34490395703](https://github.com/DanilAntyp/QueryShape/actions/runs/34490395703), full artifacts attached).

| Provider | Scenario | Mean | Ratio | Allocated |
|---|---|---:|---:|---:|
| PostgreSQL 16 | EF Core only | 880.2 µs ± 16.6 | 1.00 | 19.49 KB |
| PostgreSQL 16 | capture + scope | 920.7 µs ± 85.3 | 1.05 ± 0.05 | 23.15 KB |
| PostgreSQL 16 | capture + scope + `Analyze()` | 927.7 µs ± 48.7 | 1.05 ± 0.03 | 30.09 KB |
| SQL Server 2022 | EF Core only | 1 147.5 µs ± 6.8 | 1.00 | 26.67 KB |
| SQL Server 2022 | capture + scope | 1 166.2 µs ± 9.5 | 1.02 ± 0.01 | 30.61 KB |
| SQL Server 2022 | capture + scope + `Analyze()` | 1 190.7 µs ± 41.9 | 1.04 ± 0.02 | 37.57 KB |

In absolute terms capture costs **19–40 µs and ≈ 4 KB per two-query operation** (≈ 10–20 µs per command), and running all rules over that scope
adds a further **7–25 µs and ≈ 7 KB**. Those absolute numbers agree with the SQLite micro-benchmarks; what a real provider adds is the round trip
in the denominator, not more work in the numerator.

**Do not read the percentages as a bound.** An earlier run of the same benchmark on the same commit
([34489893743](https://github.com/DanilAntyp/QueryShape/actions/runs/34489893743)) measured capture at 1.02 for PostgreSQL and 1.05 for SQL Server —
the two providers swapped places, and the run-to-run difference is the size of the effect being measured. The runner has two physical cores and hosts
the database container beside the benchmark, so the two compete for CPU; the error bars overlap the deltas; and these are means over ten iterations of a
sequential two-query read, not a p99 under concurrent load. The honest summary is **a few percent on this workload, with the absolute per-command cost
being the number that transfers** to a different query mix.

The **< 3 % p99 target in CLAUDE.md is therefore still a target**, not a demonstrated bound: nothing here measures p99, and nothing here runs under load.

### Not measured yet

- p99 under concurrent load against a real provider, and any production-representative result size or row width.
- MySQL and Oracle overhead: both now have integration tests ([providers](providers.md)), but neither is in the benchmark.

### Next optimizations, in order of expected payoff

1. ~~Hash primitive parameter values without formatting them to strings.~~ Done.
2. ~~Resolve provider name lazily.~~ Done (cached per context).
3. Skip the counting reader when no rule needs row counts (all built-in rules currently do).
4. Pool `CapturedParameter[]` for commands with few parameters.

## Provider validation and interpretation

`ProviderOverheadBenchmarks` measures identical reads with plain EF Core and QueryShape capture plus scope-end analysis, including allocations, against disposable PostgreSQL and SQL Server containers. Run explicitly:

```sh
dotnet run -c Release --project tests/QueryShape.Benchmarks -f net10.0 -- --filter '*ProviderOverheadBenchmarks*' --exporters json markdown
```

The manual `provider-benchmarks.yml` workflow runs this on a Docker-enabled Ubuntu runner and uploads the full BenchmarkDotNet artifacts. It does not enforce a universal percentage threshold. It was executed for the first time on 2026-09-10; the results and their limits are in *Providers (2026-09-10, measured)* above. Docker is not installed on the maintainer's machine, so these numbers come from CI and cannot be reproduced locally without one.

Before a release, inspect absolute overhead, allocation differences, uncertainty and production-representative concurrency and result sizes. The provider microbenchmark is not an HTTP p99 measurement. Caller-supplied scenario metrics and operation elapsed time can provide additional evidence, but QueryShape does not infer database rows scanned or explain plans.
