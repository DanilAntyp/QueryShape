# CleanArchitecture validation results

Ran the original service and EF Core mappings from [Ardalis CleanArchitecture](https://github.com/ardalis/CleanArchitecture/tree/fbdc0951879f5e8dca1bebc273d4b28cb2934469) on 2026-09-08. Commit: `fbdc0951879f5e8dca1bebc273d4b28cb2934469`. The checkout remained unchanged.

**Both tests passed.** The combined CLI run recorded 94 complete scopes and 92 commands, with no rule diagnoses. These totals include repeated reduction trials; zero-query behavior/summary scopes are counted as scopes.

| Seeded contributors | Commands per page load | Rows returned | Repetitions |
|---:|---:|---:|---:|
| 0 | 2 | 1 | 2 |
| 1 | 2 | 2 | 2 |
| 10 | 2 | 5 | 2 |
| 100 | 2 | 5 | 2 |

The original service composes an explicit order, pagination and DTO projection over raw SQL, then separately counts contributors. Page size was four. Both repetitions agreed. The count returns one row; constant returned rows do not imply constant server work.

A negative-control candidate used the real service but incorrectly discarded contributors without phone numbers. QueryShape detected a result mismatch while the observed initial database state matched. Reduction shrank 12 contributors to **one contributor without a phone number in 19 trials**. This intentionally introduced candidate regression is not an upstream bug claim.

The CLI generated a [replay test](cleanarchitecture-reproducer/QueryShapeRegression_3edc1623f68946ae9c2125d0376e7671.cs) and [input](cleanarchitecture-reproducer/QueryShapeRegression_3edc1623f68946ae9c2125d0376e7671.json). The real service/replay callback ran during reduction. The exported generated class was not separately compiled in this external harness; generated-class compilation is independently covered by CLI integration tests.

[Machine-readable measurements and query shapes](cleanarchitecture.json) include paths to local raw report directories. [Harness and reproduction instructions](../../scripts/real-world/CleanArchitecture/README.md).

The test uses synthetic data, in-memory SQLite and EF Core 10.0.11. It sets explicit valid positive fixture IDs because the upstream ID value object rejects EF temporary negative IDs. Query code and schema mappings are unchanged. HTTP behavior, production data, other database providers and production latency were not measured.
