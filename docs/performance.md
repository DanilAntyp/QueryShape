# Performance

Measured with BenchmarkDotNet (`tests/QueryShape.Benchmarks`), Release, .NET 10.0.10, Apple M1 Pro, macOS, in-memory SQLite.
Re-run before every release:

```
dotnet run -c Release --project tests/QueryShape.Benchmarks -f net10.0 -- --filter '*' --join
```

## 0.1.0-preview.1 (2026-09-08)

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

Against any networked database the budget holds with room to spare; against an in-memory SQLite the budget cannot be met by design
because the queries themselves cost only a few microseconds. Treat 4 µs/query as the number to watch.

### Not measured yet

- p99 under concurrent load (the numbers above are single-request means); the `Meter("QueryShape")` histogram makes this observable in a real deployment.
- SQL Server / PostgreSQL providers (Testcontainers), where the row-counting reader wrapper adds one virtual call per column read.

### Next optimizations, in order of expected payoff

1. Hash primitive parameter values without formatting them to strings.
2. Resolve provider name and context id lazily.
3. Skip the counting reader when no rule needs row counts (all built-in rules currently do).
