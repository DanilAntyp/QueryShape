# QueryShape in CI

Two things make the tool visible to a whole team instead of one developer: snapshot tests that fail the build, and a report or verify table on every pull request.

## Snapshot tests

Nothing to configure: `MatchSnapshotAsync()` fails on a query regression and, with `CI=true` (set by GitHub Actions), fails on a missing snapshot instead of creating one. Commit the `__querysnapshots__` folders.

## Pull-request comments with the action

```yaml
name: QueryShape
on: [pull_request]
permissions:
  contents: read
  pull-requests: write
jobs:
  queryshape:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - uses: DanilAntyp/QueryShape@<reviewed-commit-sha> # replace with an actual release commit
        with:
          command: report
          project: tests/Shop.Tests
```

The action installs `QueryShape.Cli`, runs `dotnet queryshape report --markdown` (or `verify --format markdown`), writes the Markdown to the step summary, and upserts one comment on the pull request (it edits its own previous comment, so the thread stays clean). The job fails when the report contains Error-level diagnoses, or when `verify` finds no improvement.

To check a candidate fix in the same run:

```yaml
      - uses: DanilAntyp/QueryShape@<reviewed-commit-sha>
        with:
          command: verify
          project: tests/Shop.Tests
          test: OrderServiceTests.GetOrders_query_shape
          patch-from-diagnosis: "true"
```

## Without the action

Every command has a machine-readable form:

```
dotnet queryshape report --project tests/Shop.Tests --markdown  >> "$GITHUB_STEP_SUMMARY"
dotnet queryshape report --project tests/Shop.Tests --json      > queryshape-report.json
dotnet queryshape verify --project tests/Shop.Tests --test X --patch fix.diff --format json
```

Scope reports (`QUERYSHAPE_REPORT_DIR`) carry the snapshot outcome of each test as annotations (`snapshot.outcome`: matched / created / updated / mismatch / missing, plus `snapshot.added`, `snapshot.removed`, `snapshot.newDiagnostics`), so a CI step can list exactly which tests drifted.

`report` exits with 0 for a successful run without Error diagnoses, 1 for Error diagnoses, and 2 for a failed test run or missing scope reports. When tests fail after recording scopes, the available reports are still printed, with a partial-results warning and the test failure output on stderr. JSON stdout remains valid JSON when reports are available. An empty or nonexistent `--report-dir` is an error. `explain` also exits with 2 for failed tests or missing reports instead of claiming there are no diagnoses.

`verify` now requires behavior observations and passing tests before and after the patch. Record returned DTOs with `scope.Observe("result", result)`; use `QueryScenario` for automatic result and optional database-state observations. Old-query-count and snapshot assertions should be separate from the selected functional test. `--performance-only` explicitly permits missing observations; failed tests, changed observations and incomplete execution coverage still block approval. See [scenario contracts and reduction](scenarios.md) for `scale`/`reduce` CI commands and generated regression tests.

## Pinning, forks and scenario gates

Use a reviewed action commit SHA and an exact published `version` input matching your test packages. The source preview defaults to `0.1.0-preview.1`; do not assume a package exists on NuGet before its release. Install a supported .NET 8 or 10 runtime. The action uses an isolated tool directory and rejects floating versions. Inputs pass through environment variables and shell argument arrays. PR commenting is best-effort and skipped on fork PRs; job summaries still work.

The action supports `command: scale`, `reduce`, and `doctor` as well as `report` and `verify`. Set `test` and `sizes` for scaling; set `out` and `max-attempts` for reduction. Upload generated reproducers with your normal artifact step after reviewing their data sensitivity. JSON summaries remain available when an application assertion fails; exit 2 still fails the gate. Contract assertions yield exit 1 rather than hiding the measurements as setup failures.

Below the before/after table, `verify` lists every query shape whose execution count or returned rows changed, with the call site of the shape — the aggregate row can net a query that got cheaper against one that got dearer, and the per-shape list names which moved. Shapes that ran the same number of times for the same rows are not listed. The same list appears in the Markdown summary and under `shapes` in the JSON form; a shape whose rows were not measured in some execution reports `rows not measured` rather than a partial sum.

Verification accepts `max-commands`, `max-rows`, and `max-duration-ms` inputs. The `allow-new-warning` action input or CLI `--allow-new-warning <rule>` acknowledges changed warning identities when reviewing a query rewrite. New Error findings cannot be waived this way. Default budgets also apply per named scope for commands and returned rows, preventing total-count cancellation between operations.

## Expiring finding acceptances

Capture a clean run into a fresh directory, then run `queryshape baseline --report-dir <directory> --reason '<review decision>' --expires YYYY-MM-DD --out queryshape-acceptances.json`. Use `--id <finding-id>` for a single intentional query. Baselines require current complete capture. Commit the reviewed file and pass it to `report --acceptances <file>` or the action's `acceptances` input.

Entries contain a SHA-256 finding ID, reason, expiry date, and maximum observed occurrences. Expiry is inclusive in UTC. New or relocated query fingerprints, increased counts and expired entries remain active. Accepted findings retain their metadata in text/JSON/Markdown. These acceptances apply to the report gate; they do not rewrite query snapshots or excuse failing application tests. To change an acceptance, edit the reviewed JSON in a normal code review; baseline never overwrites an existing file.

Report gates default to Error severity. Use `report --fail-on warning` (or the action's `fail-on: warning`) to gate warnings after applying acceptances; `info` gates all active findings. Application/test failures still exit 2 regardless of acceptances.
