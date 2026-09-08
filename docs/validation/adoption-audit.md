# Adoption audit: implementation and validation

Implemented on 2026-09-08. The changes address the sixteen identified code/workflow/documentation concerns. Production evidence and actual developer adoption remain validation questions, not outcomes established by changing code.

| Audit concern | Implemented response |
|---|---|
| Fewer queries can be slower | Verification blocks duration/returned-row regressions, supports explicit budgets for tradeoffs, and exports separate metric outcomes. |
| Overstated certainty | README, CLI output, guidance and benchmark interpretation describe measured evidence and unobserved behavior. |
| Setup effort | `doctor`, non-overwriting `init`, first-run guide and runnable fixture examples. Application setup is explicitly required. |
| Heuristic warnings sound definitive | Reports label observed patterns versus heuristic risks; tracking/unbounded-read explanations state their limits. |
| Intentional findings hard to manage | Stable IDs, reasoned expiring acceptances, occurrence limits, visible accepted findings and `--fail-on` severity gates. |
| Query counts miss other work | Returned-row completeness, operation elapsed time, optional caller-supplied metrics and explicit plan/CPU/latency boundaries. |
| Weak observation coverage | Result normalization/order/state coverage in reports and behavior comparisons; no implied full-database coverage. |
| Opaque behavior failures | Opt-in bounded local differences with hidden values by default and subtree redaction. |
| Restrictive scaling | Zero-size and named generic cases, size-dependent command/row budgets and per-point custom checks. |
| Reduction needs domain logic | One-to-many parent/child shrinking helper and an external-service result-regression negative control; arbitrary domain invariants remain caller-owned. |
| Contract assertions hide reports | Available summaries survive failing tests; TRX distinguishes contract-only failures (1) from application/setup failures (2). |
| Aggregates hide changes | Executed-test identities, scenario/source/query finding identities, and per-scope command/row/time checks. |
| Unsupported overhead guarantee | Universal percentage claims removed; container-backed PostgreSQL/SQL Server benchmark harness and manual artifact-producing workflow added. |
| Narrow external validation | Added CleanArchitecture EF10/SQLite cases and reran eShopOnWeb EF8/SQLite; pinned source revisions and synthetic-data limits recorded. |
| CLI/CI friction | .NET 8 and 10 CLI packaging, installation smoke test, pinned isolated action installation, argument-array input handling, fork-safe best-effort comments, scale/reduce/doctor and budget inputs. |
| Unclear primary workflow | README centers on protecting an existing EF Core operation in tests and PRs; advanced features are introduced afterward. |

## Checks completed

- Solution build: **zero warnings, zero errors**, including both CLI targets and the provider benchmark harness.
- Repository fast suite: **454 passed, 18 skipped**, final built binaries. Skips: Docker provider tests and tests incompatible with local net8-to-net10 roll-forward.
- Slow CLI integration suite: **five passed**, including actual Git-worktree patch verification and generated-regression compilation. Together with the fast suite: **459 passing repository tests**.
- eShopOnWeb: **15 passed**, 180 complete scopes, 240 commands. The lookup row-growth contract still correctly fails; CLI scale exits 1. [Fresh rerun measurements](eshoponweb-audit-rerun.json).
- CleanArchitecture: **two passed**, 94 complete scopes, 92 commands, zero rule diagnoses. Its intentionally wrong candidate reduced from 12 contributors to one in 19 trials. [Results](cleanarchitecture.md).
- Packaging: built local packages and installed the CLI from a local-only NuGet feed into a temporary tool directory. Installed `--help`, `doctor` against current reports, and `init` succeeded.
- Acceptance workflow: `report --fail-on warning` returned 1 on the fresh eShop capture. A smoke-test baseline contained 237 distinct finding IDs covering 280 occurrences. Applying it returned 0 while preserving all 280 diagnoses with accepted disposition, reasons and expiry. This temporary blanket baseline tests the mechanism; it is not a recommendation to accept those findings without review.
- Action/workflow YAML parsed; Bash run blocks passed `bash -n`; `git diff --check` passed.

Local detailed test log: `/private/tmp/queryshape-audit-final-tests.log`. Raw external report paths are recorded in their JSON evidence files. Temporary installation and baseline artifacts are smoke-test outputs, not published releases.

## Remaining external validation

Docker is not installed here, so provider benchmark numbers remain pending. The added workflow can produce those artifacts on a Docker-enabled runner. Native .NET 8/10 Linux/Windows CI has not been executed by this local session. Neither two synthetic-data application trials nor the new onboarding flow establish production latency improvements, population-wide diagnostic accuracy, or developer adoption. These limits remain explicit in the [validation matrix](README.md).
