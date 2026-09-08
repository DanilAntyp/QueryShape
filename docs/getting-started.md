# First useful result

Choose an existing integration test that executes a real EF Core operation. Start with one operation; test fixture design is required for the stronger scaling and reduction features.

1. Add `QueryShape.Testing` to that test project using the [source-preview installation guide](installation.md). It references the core capture library. The CLI and libraries should use the same release. You can build local packages or run the CLI from source with `-f net10.0`.
2. Add `.UseQueryShape()` to the `DbContextOptionsBuilder` used by the fixture. Keep the production provider when provider behavior matters.
3. Create/seed the fixture, then open a named `QueryShapeScope` around the service call. Materialize returned DTOs inside the scope.
4. Assert that at least one command was captured. Record `scope.Observe("result", dto)` and keep your functional assertions. Add `MatchSnapshotAsync()` for a query-shape contract.
5. Run `queryshape doctor --project <test-project> --test <test-name>`. Fix missing commands/scopes before interpreting an empty diagnostic list as success.

`queryshape init --out QueryShapeSmokeTests.cs --framework xunit` generates the wrapper, with a deliberately unimplemented operation hook. Wire your existing fixture and service to it; the scaffold cannot infer your application's database lifecycle. It never modifies packages or existing files. NUnit/MSTest attributes are available through `--framework`.

## Reusable fixtures

For scaling, make the fixture implement `IAsyncDisposable`. `PrepareAsync(input, ct)` creates an isolated database/context and seeds it; it must clean up if setup throws. `ExecuteAsync(fixture, ct)` runs the real service. `ObserveStateAsync` reads a stable persisted-state projection using no tracking or a fresh context. `StateDescription` explains what was observed.

Use `QueryScenario<int, MyFixture, MyDto[]>` for size-based data. Use `RunCasesAsync` with named inputs for distributions and multiple dimensions. Existing runnable examples are in `tests/QueryShape.Testing.Tests/AdoptionTests.cs`, `ScenarioTests.cs`, and the [real-repository harnesses](../scripts/real-world).

## Common first-run problems

| Symptom | What to check |
|---|---|
| No scope reports | The selected test ran and opens a scope; the report directory is writable. |
| Scope exists but no commands | `UseQueryShape()` is on the context actually used; the operation did not return solely from a cache. |
| HTTP operation outside the test scope | For TestServer set `PreserveExecutionContext = true`. |
| Incomplete capture | Finish/dispose readers, enable capture, and inspect overflow or failed commands. Old reports need recapturing for verify. |
| No test identities | Use a `dotnet test` runner that supports TRX. Check the filter and skipped tests. |
| Worktree test fails | Review test output; ignored local settings and data are not copied automatically. Use deterministic fixture configuration or environment variables. |
| Nondeterministic behavior | Normalize only irrelevant generated values. Preserve fields and ordering that matter. |
| Intentional warnings | Add scoped, reasoned, expiring acceptances for report gates. Keep snapshots as independent contracts. |

`verify` requires Git, passing functional tests, current complete capture, stable test identities and explicit observations by default. It repeats baseline and patched executions and creates a detached worktree. `--allow-dirty` carries tracked changes and non-ignored untracked files. `--keep-worktree` retains the worktree and reports for debugging. No ignored secrets are copied automatically.
