# Validation evidence and remaining gaps

The current corpus covers two external application repositories, plus deliberately bad/fixed sample operations and provider integration tests. External datasets are synthetic. Passing tests are evidence for those executions, not a production guarantee or an adoption study.

To rerun both pinned external applications on Linux with native .NET 8 and .NET 10 runtimes, use the repository's **Real application validation** workflow (`workflow_dispatch`). Each application uploads scope reports and TRX results. The harness READMEs also provide local commands.

| Corpus | What is exercised | Boundaries |
|---|---|---|
| [eShopOnWeb](eshoponweb-scenarios.md), pinned `4da8212117e87d808d4bbc7da6286fd2147ce606` | Catalog, basket, checkout, history; growth contracts; no-tracking result/state comparison; threshold reduction | EF8 with SQLite key-generation adaptation; cache and HTTP paths excluded |
| [CleanArchitecture results](cleanarchitecture.md), pinned `fbdc0951879f5e8dca1bebc273d4b28cb2934469` | Ordered projection over raw SQL; empty and nullable owned-data cases; reduction of an intentionally incorrect candidate | EF10/SQLite; explicit valid fixture IDs; candidate regression is a negative control, not an upstream finding |
| QueryShape sample/test fixtures | Rule detection, query snapshots, batched growth, lost rows/updates, behavior comparisons and actual worktree verification | Designed test cases; cannot establish external false-positive or false-negative rates |
| SQL Server/PostgreSQL provider tests | Provider shapes, correlation, N+1 | Require Docker; skipped locally when unavailable |
| Provider overhead benchmark workflow | Plain EF versus capture/scope analysis; allocation and duration distributions | Harness added; provider results pending a Docker-enabled run |

The adoption audit added regression checks for fewer-but-slower queries, returned-row increases, legitimate budgeted split queries, relocated findings, test identity replacement, acceptance expiry/counts, missing instrumentation, local redaction and foreign-key-preserving shrink candidates.

## Release evidence still required

- Run the provider benchmark workflow and retain the full artifacts, environment, workload and uncertainty. No universal percentage overhead claim is justified by the existing SQLite results.
- Run CI on actual .NET 8 and .NET 10 runtimes on Linux and Windows. Local .NET 8 roll-forward does not substitute for that matrix.
- Validate more applications and production-representative datasets. Record actionable findings, intentional patterns flagged, missed issues, integration effort and confirmed improvements. Two applications do not establish precision/recall or typical adoption effort.
- Observe actual developers completing the first-run guide and maintaining contracts over time. Usability changes alone do not establish that developers will adopt the tool.

The source changes address the identified implementation and documentation problems. Production evidence and user research remain external validation work, explicitly separated from implemented capabilities.
