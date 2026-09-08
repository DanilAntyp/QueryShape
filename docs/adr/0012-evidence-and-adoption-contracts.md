# ADR-0012: evidence, verification budgets and incremental adoption

Status: accepted, 2026-09-08. The user requested correction of the adoption audit, including previously implemented scenario features.

Verification is a check of selected measurements and observations, not a proof of universal behavior or production speed. It repeats both baseline and candidate, compares test identities and scope multiplicities, requires complete current captures, and checks every candidate against explicit budgets. Defaults prohibit increased commands and returned rows and materially increased command time. A command/row budget can explicitly permit tradeoffs. Warning identity exceptions require an explicit rule option; new Errors remain blocking. Metric outcomes are separate in JSON.

Finding identities contain the scenario, rule, call-site member/file (without line numbers or checkout roots), and sorted SQL fingerprints. New locations cannot cancel old ones through aggregate counts. Expiring JSON acceptances retain reasons and maximum occurrences; reports show accepted findings instead of deleting evidence. They govern report gates, not independent snapshot assertions.

Scenario assertions throw a recognizable contract exception. The CLI renders available summaries on all test failures, distinguishes contract-only failures from other failures using TRX, and preserves exit 2 for application/setup failures. Local behavior differences require explicit opt-in, hide values by default and support subtree redaction. Reported coverage describes the supplied projections, not inferred full-database coverage.

Scaling supports named generic inputs, zero-sized cases, size-dependent budgets, custom per-point checks, operation elapsed time and optional caller-supplied metrics. The tool does not infer execution-plan cost. Relational shrinking handles one parent/child relationship; arbitrary domain invariants remain caller-owned.

The CLI now packages net8.0 and net10.0 targets. Doctor diagnoses an existing test; init scaffolds a hook rather than pretending to infer fixtures. CI passes inputs through environment variables/argument arrays and pins the package version. Provider benchmarks are an explicit container-backed workflow, with unexecuted results marked pending.

No automatic suppression of new findings, automatic source transmission, universal overhead guarantee, database-plan inference or application-wide equivalence claim is introduced.
