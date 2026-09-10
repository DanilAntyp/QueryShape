# Validation evidence and remaining gaps

The current corpus covers three external application repositories, plus deliberately bad/fixed sample operations and provider integration tests. External datasets are synthetic. Passing tests are evidence for those executions, not a production guarantee or an adoption study.

To rerun both pinned external applications on Linux with native .NET 8 and .NET 10 runtimes, use the repository's **Real application validation** workflow (`workflow_dispatch`). Each application uploads scope reports and TRX results. The harness READMEs also provide local commands.

[Hosted application run on 2026-09-09](https://github.com/DanilAntyp/QueryShape/actions/runs/34411411094): all 15 eShopOnWeb tests passed on native .NET 8, and both CleanArchitecture tests and all 7 Jellyfin tests passed on .NET 10, against QueryShape commit `2804a86`. All three jobs uploaded their scope reports and TRX results. ([Earlier run without Jellyfin, 2026-09-08](https://github.com/DanilAntyp/QueryShape/actions/runs/34278103529).)

| Corpus | What is exercised | Boundaries |
|---|---|---|
| [eShopOnWeb](eshoponweb-scenarios.md), pinned `4da8212117e87d808d4bbc7da6286fd2147ce606` | Catalog, basket, checkout, history; growth contracts; no-tracking result/state comparison; threshold reduction | EF8 with SQLite key-generation adaptation; cache and HTTP paths excluded |
| [CleanArchitecture results](cleanarchitecture.md), pinned `fbdc0951879f5e8dca1bebc273d4b28cb2934469` | Ordered projection over raw SQL; empty and nullable owned-data cases; reduction of an intentionally incorrect candidate | EF10/SQLite; explicit valid fixture IDs; candidate regression is a negative control, not an upstream finding |
| [Jellyfin results](jellyfin.md), pinned `cf09de60e4e5844ad181d7ef9019151c54969d44` | Library page, latest items, by-name and id-only paths through the real `BaseItemRepository`; row multiplication of five collection includes; upstream's own split-query branch as the measured alternative | EF10/SQLite native, no mapping adaptation; synthetic 24-movie library with a fixed per-item fan-out; the flagged pattern is deliberate upstream (EF's own warning is suppressed), so it is a measurement, not a bug report |
| QueryShape sample/test fixtures | Rule detection, query snapshots, batched growth, lost rows/updates, behavior comparisons and actual worktree verification | Designed test cases; cannot establish external false-positive or false-negative rates |
| SQL Server/PostgreSQL provider tests | Provider shapes, correlation, N+1 | Require Docker; skipped locally when unavailable |
| Provider overhead benchmark workflow | Plain EF versus capture, and capture plus analysis, on PostgreSQL 16 and SQL Server 2022 containers; allocation and duration distributions | Executed 2026-09-10; means over ten sequential iterations on a two-core runner hosting the database beside the benchmark, not p99 under load |

The adoption audit added regression checks for fewer-but-slower queries, returned-row increases, legitimate budgeted split queries, relocated findings, test identity replacement, acceptance expiry/counts, missing instrumentation, local redaction and foreign-key-preserving shrink candidates.

## Hosted runtime validation

[CI passed on Linux and Windows on 2026-09-08](https://github.com/DanilAntyp/QueryShape/actions/runs/34279175865), against commit `87cd685`. Both jobs ran tests on native .NET 8 and .NET 10 runtimes. Ubuntu additionally passed the PostgreSQL/SQL Server container tests, all five slow CLI integration tests, and package creation. Windows skips the Linux-only provider containers. The README scaling and HTTP demos passed in a separate job.

## Release evidence still required

- ~~Run the provider benchmark workflow and retain the full artifacts, environment, workload and uncertainty.~~ Done on 2026-09-10 ([run 34490395703](https://github.com/DanilAntyp/QueryShape/actions/runs/34490395703)): PostgreSQL 16 and SQL Server 2022, capture 19–40 µs and ≈ 4 KB per two-query read, analysis a further 7–25 µs. Still no universal percentage claim: a repeat run on the same commit moved the two providers' ratios past each other, the runner shares two cores with the database container, and nothing here measures p99 or concurrency. [Numbers and limits](../performance.md).
- Validate more applications and production-representative datasets. Record actionable findings, intentional patterns flagged, missed issues, integration effort and confirmed improvements. Three applications do not establish precision/recall or typical adoption effort.
- Observe actual developers completing the first-run guide and maintaining contracts over time. Usability changes alone do not establish that developers will adopt the tool.

The source changes address the identified implementation and documentation problems. Production evidence and user research remain external validation work, explicitly separated from implemented capabilities.
