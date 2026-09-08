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
      - uses: queryshape/QueryShape@main          # pin a release tag once published
        with:
          command: report
          project: tests/Shop.Tests
```

The action installs `QueryShape.Cli`, runs `dotnet queryshape report --markdown` (or `verify --format markdown`), writes the Markdown to the step summary, and upserts one comment on the pull request (it edits its own previous comment, so the thread stays clean). The job fails when the report contains Error-level diagnoses, or when `verify` finds no improvement.

To prove a fix in the same run:

```yaml
      - uses: queryshape/QueryShape@main
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
