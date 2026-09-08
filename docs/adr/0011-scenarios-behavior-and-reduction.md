# ADR-0011: Scenario contracts, behavior verification, and reduction

Date: 2026-09-08. Status: accepted (user requested all three capabilities).

Extend `QueryShape.Testing` with developer-defined, disposable scenario fixtures. An integer input supports scaling dimensions; arbitrary immutable inputs support data reduction. Core interceptors and rule IDs stay unchanged. New APIs use existing capture and annotations, with no new Core package dependencies.

Scaling checks exact command/row contracts across measured sizes and repetitions, excluding setup and observation queries. They do not infer mathematical complexity or production latency. All-zero and incomplete capture are failures.

Behavior comparison uses canonical JSON digests of explicit DTO/state projections, preserves array order unless root-array multiset comparison is requested, and reports only the observation that changed. Scenario execution observes state outside the measured scope. Selected observations, initial data and scope/test coverage must match before `verify` accepts an improvement. Verification rejects all failed test runs, replacing the previous behavior that tolerated failed old-shape assertions. `--performance-only` permits missing observations with an explicit limitation; it does not waive passing tests, existing observations or complete capture. TRX records establish executed-test counts.

Reduction accepts application-supplied candidates, a strictly decreasing complexity metric, and a named failure predicate. Invalid datasets are skipped; exceptions propagate. Confirmation runs and attempt/candidate limits bound the search. Completion denotes a local minimum relative to the supplied candidates, not a global minimum. Explicit artifact generation emits reduced JSON and a compilable framework-specific regression test using an application-provided replay helper; QueryShape cannot infer business invariants or reconstruct arbitrary application setup.

`scale` and `reduce` run selected scenario tests and render annotation reports as text/JSON. `verify`/`fix` compare behavior on every patched run. No external service or LLM is required.
