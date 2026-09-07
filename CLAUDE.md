# QueryShape — EF Core query pathology detector

> Name decided 2026-09-07: **QueryShape** (namespaces, packages, CLI `dotnet queryshape`, rule ids `QS0xx`, OTel prefix `queryshape.`). See ADR-0001.

You are building an open-source .NET library that catches slow and dangerous Entity Framework Core queries, explains them in terms of the **LINQ that produced them** (not the SQL that came out), and stops regressions from reaching production.

This file is the source of truth for scope, architecture and conventions. Read it fully before touching code. When something here is ambiguous or turns out to be wrong in practice, stop and ask instead of guessing — then update this file with the decision.

---

## 1. What we are building (and not building)

Three features, in this order. Each must be independently shippable and useful on its own.

| # | Feature | One-line outcome |
|---|---------|------------------|
| 1 | **Query snapshot testing** | A test that fails in CI when a code change makes EF Core issue more/different queries than before. "Jest snapshots, but for SQL." |
| 2 | **Diagnosis + measured fix** | Every detected pathology comes with a concrete, applicable code fix, and a harness that proves the improvement with before/after numbers. |
| 3 | **OpenTelemetry enrichment** | Diagnoses appear as attributes/events on the spans teams already look at in Datadog / App Insights / Grafana. We are the "why" layer, not another APM. |

### Explicit non-goals (do not build these, even if tempting)
- No web dashboard, no charts UI, no hosted service. Console/CLI/JSON output only.
- No general-purpose profiler. We only care about EF Core (and the raw ADO.NET escape hatch — see 4.4).
- No support for EF6 / .NET Framework, or EF Core 9.
- No LLM call in the hot path of a running app. LLM usage is opt-in, CLI-side, and always behind a flag (see 6.2).

---

## 2. Tech decisions (fixed)

- **Target**: `net8.0` and `net10.0` multi-target for libraries. EF Core 8 and 10 — reference the matching EF Core major per target via a `Condition` on `TargetFramework` in the csproj. Do not support EF Core 7, EF Core 9 (STS, out of support since May 2026), or lower. .NET 8 reaches EOL in November 2026: plan to drop `net8.0` in the first release after that, leaving `net10.0` as the sole target.
- **Runtimes for tests**: locally, run with `<RollForward>Major</RollForward>` in test projects (only .NET 10 needs to be installed). In CI, install every targeted runtime via `actions/setup-dotnet` and run `dotnet test -f net8.0` and `dotnet test -f net10.0` as separate steps — roll-forward hides exactly the runtime/EF-version differences this library cares about.
- **API verification**: before writing the capture layer, check `IQueryExpressionInterceptor`, `IDbCommandInterceptor` and `QueryExpressionEventData` in the EF Core 10 source against the assumptions in section 4.1 and record any differences in an ADR.
- **Language**: C# 12+, nullable enabled, `TreatWarningsAsErrors` on, implicit usings on.
- **Testing**: xUnit + FluentAssertions. Fast tests use **SQLite in-memory** via EF Core. Provider-specific behavior (SQL Server, PostgreSQL) uses **Testcontainers**; these tests are tagged `[Trait("Category","Integration")]` and skipped when Docker is unavailable.
- **Serialization**: `System.Text.Json` only. Snapshot files must be deterministic (sorted keys, stable ordering, `\n` line endings, trailing newline).
- **Logging**: `Microsoft.Extensions.Logging` abstractions. Never `Console.WriteLine` inside library code.
- **Telemetry**: `System.Diagnostics.Activity` / `ActivitySource` (OTel-native in .NET). Do not take a dependency on the OpenTelemetry SDK in the core package — only on `System.Diagnostics.DiagnosticSource`.
- **Public API**: every public type is in the `QueryShape` namespace hierarchy. Use `PublicAPI.Shipped.txt`/`Unshipped.txt` (Microsoft.CodeAnalysis.PublicApiAnalyzers) so API changes are deliberate.
- **License**: MIT.

---

## 3. Repository layout

```
queryshape/
├─ CLAUDE.md                      ← this file
├─ Directory.Build.props          ← shared: nullable, warnings-as-errors, versioning
├─ QueryShape.sln
├─ src/
│  ├─ QueryShape.Core/                ← capture, normalization, rules engine, diagnosis model. No test/OTel deps.
│  ├─ QueryShape.Testing/             ← snapshot testing API for xUnit/NUnit/MSTest (feature #1)
│  ├─ QueryShape.OpenTelemetry/       ← Activity enrichment (feature #3)
│  └─ QueryShape.Cli/                 ← `dotnet queryshape` tool: verify fixes, print reports, update snapshots (feature #2)
├─ tests/
│  ├─ QueryShape.Core.Tests/
│  ├─ QueryShape.Testing.Tests/
│  ├─ QueryShape.OpenTelemetry.Tests/
│  └─ QueryShape.SampleApp/           ← small ASP.NET Core app with deliberately bad queries; used by tests AND as the README demo
├─ docs/
│  ├─ rules/                      ← one markdown file per rule: what, why EF Core does this, fix, example
│  └─ adr/                        ← architecture decision records, numbered
└─ .github/workflows/ci.yml
```

`QueryShape.SampleApp` must contain at least one instance of **every** rule in section 5, each in its own clearly named endpoint (e.g. `GET /bad/n-plus-one`). Tests assert on these; the README demos them.

---

## 4. Core architecture (`QueryShape.Core`)

### 4.1 Capture

Hook EF Core via interceptors registered on `DbContextOptionsBuilder`:

- `IQueryExpressionInterceptor.QueryCompilationStarting` — gives us the **LINQ expression tree** before translation. Store a normalized string form of the expression and a hash. This is our link back to C#.
- `IDbCommandInterceptor` (`ReaderExecuting/Executed`, `NonQueryExecuting/Executed`, `ScalarExecuting/Executed`, and the async variants) — gives us the SQL, parameters, duration, and `CommandEventData` (with `CommandId`, `ConnectionId`, `Context`).
- `DbContext.SaveChanges` interceptors for write-side rules.

Correlate expression → command via `QueryExpressionEventData` and the `CommandSource`/tags. Where EF Core does not expose a direct link, fall back to matching on compilation order within the same `DbContext` instance and document the limitation in an ADR.

**Call-site capture**: capturing a stack trace on every query is too expensive for production. Strategy:
1. Default: capture call site only when `QueryShapeOptions.CaptureCallSites = true` (on by default in test mode, off in production).
2. Cheap alternative always available: honor `.TagWith(...)`; provide a `TagWithCallSite()` extension using `[CallerFilePath]`/`[CallerLineNumber]`/`[CallerMemberName]` that injects the tag into the SQL comment. This is zero-cost and works in production.
3. Stack walking, when enabled, must skip frames in `Microsoft.EntityFrameworkCore.*`, `System.*`, `QueryShape.*` and report the first user frame as `file:line member`.

### 4.2 Scope

A **scope** is the unit of analysis: one HTTP request, one test, or one explicit `using var scope = QueryShapeScope.Begin()`. Scopes are `AsyncLocal`-based so they flow across `await`. Every captured command is attached to the current scope (or a "no scope" bucket). All rules run **per scope**; N+1 detection is meaningless without it.

Provide ASP.NET Core middleware (`app.UseQueryShape()`) that opens a scope per request and closes it after the response.

### 4.3 Normalization

Two normalized forms of every SQL command, both deterministic:
- **Shape**: SQL with parameter *values* stripped (keep parameter names), whitespace collapsed, EF Core's auto-generated aliases (`[t]`, `[o0]`…) canonicalized to positional aliases, tags/comments removed. Two queries with the same shape are "the same query with different arguments."
- **Fingerprint**: SHA-256 of the shape, first 12 hex chars. Used in snapshots and OTel attributes.

Also record: provider name, `CommandType`, rows affected/returned where available, duration, whether tracking was on, `QuerySplittingBehavior`.

### 4.4 Raw ADO.NET escape hatch

Also register a `DbConnection`-level interceptor so `FromSqlRaw`, Dapper and hand-written `DbCommand` usage are captured with the same shape/fingerprint pipeline. Mark them `Source = Raw`. Rules that need an expression tree skip raw commands; count/shape-based rules include them.

### 4.5 Diagnosis model

```csharp
public sealed record Diagnosis(
    string RuleId,          // e.g. "QS001"
    Severity Severity,      // Info | Warning | Error
    string Title,           // "N+1 query in OrderService.GetOrders"
    string Explanation,     // WHY EF Core produced this — plain English, 2–5 sentences, no jargon dump
    CallSite? CallSite,     // file:line, member — null if unknown
    IReadOnlyList<string> Fingerprints,
    Evidence Evidence,      // counts, timings, sample SQL — whatever the rule used
    Fix? SuggestedFix);     // see 6.1

public sealed record Fix(
    string Summary,                 // "Add .Include(c => c.Orders)"
    FixKind Kind,                   // CodeChange | ConfigChange | SchemaChange | Manual
    string? BeforeSnippet,
    string? AfterSnippet,
    string? UnifiedDiff,            // when we can produce a real patch
    string Rationale,
    string DocsUrl);                // link to docs/rules/QSxxx.md on GitHub
```

Every `Explanation` must answer "why did EF Core do this?" in terms a junior understands. Look at `docs/rules/` for tone. Never emit an explanation that just restates the title.

---

## 5. Rules (initial set)

Each rule is a class implementing `IRule` with `Analyze(Scope) -> IEnumerable<Diagnosis>`. Rules are pure functions of the scope — no I/O. Each rule ships with: a unit test on synthetic scopes, an integration test against `QueryShape.SampleApp`, and a `docs/rules/QSxxx.md`.

| Id | Name | Detection | Default severity |
|----|------|-----------|------------------|
| QS001 | N+1 query | Same shape executed ≥ `NPlusOneThreshold` (default 5) times in one scope with varying parameters | Error |
| QS002 | Cartesian explosion | Single query with ≥2 collection `Include`s (or JOINs producing row multiplication) and rows returned ≫ distinct root entities | Error |
| QS003 | Client-side evaluation | Expression tree contains nodes EF Core could not translate (detect via `QueryCompilationStarting` + provider warnings), or `AsEnumerable()`/`ToList()` before `Where`/`Select`/`OrderBy` | Error |
| QS004 | Unbounded result set | Query with no `Take`/`First`/`Single`/`Any`/`Count` and no `Where`, or returned rows > `UnboundedRowThreshold` (default 1000) | Warning |
| QS005 | Tracking on read-only query | Tracked query whose results are never modified in the scope (no `SaveChanges` touching those entity types) | Info |
| QS006 | Missing split query candidate | Multiple collection `Include`s without `AsSplitQuery()` and row count suggests explosion | Warning |
| QS007 | `Contains` on large collection | `Where(x => list.Contains(x.Id))` with `list.Count` > 500 → giant `IN (...)` or parameter limit risk | Warning |
| QS008 | Duplicate identical query | Same shape **and** same parameters executed >1 time in a scope (should be cached/reused) | Warning |
| QS009 | Query in loop over navigation | Heuristic on call site: same call site issuing queries repeatedly within one scope | Warning |
| QS010 | Raw SQL with string concatenation | `FromSqlRaw`/raw command whose text varies across executions in a way that suggests interpolated values (not parameters) | Error (security) |

Start with QS001, QS003, QS004, QS008 (simplest, highest value). Add the rest once the pipeline is solid. Do not add rules beyond this table without an ADR.

---

## 6. Feature #1 — Query snapshot testing (`QueryShape.Testing`)

### 6.1 Developer experience (this is the contract — don't drift from it)

```csharp
[Fact]
public async Task GetOrders_query_shape()
{
    using var scope = QueryShapeScope.Begin();          // or QueryShapeTest.Begin() in xUnit fixture
    await _sut.GetOrdersAsync(customerId: 42);
    await scope.MatchSnapshotAsync();               // compares against __querysnapshots__/<TestClass>.<TestName>.json
}
```

Also support budgets without a snapshot file:

```csharp
[Fact, QueryBudget(MaxQueries = 2, MaxDurationMs = 200, FailOn = Severity.Error)]
public async Task GetOrders_stays_within_budget() { ... }
```

### 6.2 Snapshot file format

`__querysnapshots__/{TestClass}.{TestName}.json`, next to the test source file (resolve via `[CallerFilePath]`). Contents:

```json
{
  "version": 1,
  "queryCount": 2,
  "queries": [
    { "fingerprint": "a1b2c3d4e5f6", "shape": "SELECT [c].[Id], ... FROM [Customers] AS [c] WHERE [c].[Id] = @p0", "source": "Linq", "tags": ["OrderService.GetOrdersAsync"] }
  ],
  "diagnostics": [ { "ruleId": "QS005", "severity": "Info" } ]
}
```

Rules for comparison:
- **Fails** when: query count changes, any fingerprint is added/removed, or a diagnostic of severity ≥ `FailOn` (default `Warning`) appears that was not in the snapshot.
- **Does not fail** on: timing changes, parameter values, ordering of queries (compare as multiset), diagnostics that already existed in the snapshot (they're known debt — track them, don't block).
- Failure message must show a readable diff: which queries were added/removed, the call site if known, and the diagnosis + suggested fix for any new pathology. This message is the product; make it excellent.
- Update snapshots with env var `QUERYSHAPE_UPDATE_SNAPSHOTS=1` or `dotnet queryshape snapshots update`. Never auto-update silently. Missing snapshot on first run: write it and **pass**, with a clear log line — same behavior as Jest.
- CI mode (`CI=true` env var present): a missing snapshot is a **failure**, not a write.

### 6.3 Test framework support

xUnit first-class. NUnit and MSTest via the same core with thin adapters. Test name/class resolution must not depend on reflection over the framework's internals — use caller-info attributes and an explicit optional `name:` parameter.

---

## 7. Feature #2 — Diagnosis + measured fix

### 7.1 Fix generation (rule-based, deterministic)

Each rule owns its fix template(s). Fixes must be **specific**, referencing the actual entity/navigation/call-site names from the evidence — never generic advice. Examples:

- QS001 → `Add .Include(c => c.Orders) to the query at OrderService.cs:42` with before/after snippet reconstructed from the expression tree.
- QS003 → identify the untranslatable node, propose the translatable rewrite (e.g. `EF.Functions.Like`, `DateOnly` handling), or an explicit `AsEnumerable()` boundary if intentional.
- QS005 → `.AsNoTracking()` plus, for DbContext-wide, `UseQueryTrackingBehavior(NoTracking)`.
- QS007 → chunking helper or temp-table/`OPENJSON` pattern depending on provider.

When we can locate the exact source line (call site known + file readable), produce a real **unified diff** against the source file. Otherwise produce before/after snippets and mark `UnifiedDiff = null`.

### 7.2 Optional LLM layer (CLI only, off by default)

`dotnet queryshape explain --llm` may send the **diagnosis JSON + the relevant source method** (never the whole repo, never connection strings, never parameter values) to an LLM to produce a richer explanation or a diff when the rule-based template can't. Requirements:
- Requires an explicit API key env var; absent key → feature disabled with a clear message.
- Print exactly what is being sent (`--show-prompt`) so users can audit it.
- LLM output is always labeled as such and never trusted for the *detection* — only for explanation/fix drafting.
- Model/provider behind an interface; first implementation targets the Anthropic Messages API. Keep this thin.

### 7.3 Verification harness (`dotnet queryshape verify`)

The point of this feature is **proof**, not advice.

```
dotnet queryshape verify --test "OrderServiceTests.GetOrders_query_shape" --patch fix.diff
```

1. Run the named test(s) with QueryShape capture → baseline metrics (query count, total duration, per-fingerprint counts, diagnostics).
2. Apply the patch to a **clean git worktree** (never the user's working copy; refuse if the repo is dirty unless `--allow-dirty`).
3. Rebuild and rerun the same tests → after metrics.
4. Print a before/after table and exit code: `0` if improved and no new Error-severity diagnostics, `1` otherwise.

```
Fix: Add .Include(c => c.Orders) at OrderService.cs:42

                 before    after     Δ
queries            341        2   -339
duration (ms)     1820       41   -98%
QS001 N+1           1        0      ✓
new diagnostics      -        0      ✓
```

Also support `--patch-from-diagnosis` to use the fix QueryShape itself proposed. Duration numbers are noisy: run the "after" leg at least 3× and report the median; print a note when the delta is below noise.

---

## 8. Feature #3 — OpenTelemetry enrichment (`QueryShape.OpenTelemetry`)

Philosophy: **never create a competing trace**. Attach to what already exists.

- EF Core and the ADO.NET providers (Npgsql, Microsoft.Data.SqlClient with OTel enabled) already emit spans. When a command executes, find `Activity.Current` and add tags:
  - `queryshape.fingerprint`, `queryshape.shape` (truncated to 1 KB), `queryshape.source` (`Linq`|`Raw`), `queryshape.callsite` (if known), `queryshape.tags`.
- At scope end (request end), for each diagnosis add an **Activity event** to the request's root span: name `queryshape.diagnosis`, attributes `queryshape.rule_id`, `queryshape.severity`, `queryshape.title`, `queryshape.callsite`, `queryshape.fix.summary`, `queryshape.docs_url`. Also set span tags `queryshape.diagnosis_count` and `queryshape.max_severity` so users can filter traces by "had an Error-level QueryShape finding".
- If no ambient activity exists, optionally start our own `ActivitySource("QueryShape")` span **only** when `QueryShapeOptions.CreateActivitiesWhenNoneExist = true` (default false).
- Respect sampling: if `Activity.Current` is not recorded, do nothing beyond the cheap tag set.
- Provide `services.AddQueryShape(o => ...)` + `optionsBuilder.UseQueryShape()` + `app.UseQueryShape()` as the entire setup. Three lines in `Program.cs`, documented in the README.
- Emit metrics via `System.Diagnostics.Metrics.Meter("QueryShape")`: `queryshape.queries` (counter, tags: fingerprint, source), `queryshape.diagnoses` (counter, tags: rule_id, severity), `queryshape.query.duration` (histogram). Cardinality warning: fingerprint tag on metrics is opt-in.

Verify with a test that uses the OpenTelemetry SDK's in-memory exporter (test project may depend on the OTel SDK; the library may not) and asserts the exact attributes on the exported spans for each SampleApp endpoint.

---

## 9. Performance & safety budget

This runs inside other people's production apps. Treat it that way.

- Capture overhead target: **< 3% p99 latency** on the SampleApp under load with call-site capture off; measure with BenchmarkDotNet in `tests/QueryShape.Benchmarks` and record numbers in `docs/performance.md` on every release.
- Never throw out of an interceptor. Catch everything, log at `Debug`, increment an internal error counter, continue. A bug in QueryShape must never fail a user's query.
- Never keep unbounded state. Scopes are bounded (`MaxCommandsPerScope`, default 10 000, then stop recording and flag `QS_OVERFLOW`). No static collections that grow forever.
- Never log or export **parameter values** by default (PII). `IncludeParameterValues` exists, is off, and is documented as a PII risk.
- Never read source files or walk stack traces in production unless explicitly enabled.

---

## 10. Working conventions for you (Claude Code)

- **Work in this order**: Core capture + normalization + QS001/003/004/008 → `QueryShape.Testing` snapshots → SampleApp + CI → OTel enrichment → remaining rules → CLI `verify` → optional LLM layer. Do not start a later phase until the earlier one has green tests and a passing CI run.
- **Tests first for rules**: write the failing SampleApp integration test that demonstrates the pathology, then make the rule detect it.
- **Commit small**, conventional commits (`feat(core): …`, `fix(testing): …`, `docs(rules): …`). One rule = one PR-sized commit.
- **ADRs**: any deviation from this file, any non-obvious design choice (e.g. how expression→command correlation works) gets a numbered ADR in `docs/adr/`. Keep them short.
- **Docs are part of done**: a rule without `docs/rules/QSxxx.md` is not done. README must always reflect the real, working setup — run the README commands as part of CI where feasible.
- **Ask when**: EF Core internals don't expose what a section here assumes; a rule's false-positive rate looks high on the SampleApp; anything would require a new NuGet dependency in `QueryShape.Core`.
- **Don't**: add a UI, add abstractions "for later", support EF Core < 8 or EF Core 9, silently downgrade a spec item, or invent rules not in section 5.

---

## 11. Definition of done — v0.1

- [ ] `QueryShape.Core`, `QueryShape.Testing`, `QueryShape.OpenTelemetry` build for net8.0/net10.0 against EF Core 8 and 10, warnings-as-errors, public API tracked.
- [ ] Rules QS001, QS003, QS004, QS005, QS008 implemented, documented, tested (unit + SampleApp integration on SQLite; SQL Server + Postgres via Testcontainers in CI).
- [ ] Snapshot testing works in xUnit with the exact DX in 6.1; failure messages reviewed for readability against every SampleApp endpoint.
- [ ] `CI=true` behavior, `QUERYSHAPE_UPDATE_SNAPSHOTS`, and `QueryBudget` attribute all covered by tests.
- [ ] OTel: spans from SampleApp requests carry `queryshape.*` tags and `queryshape.diagnosis` events; verified with in-memory exporter.
- [ ] `dotnet queryshape verify` produces the before/after table for at least the QS001 SampleApp case.
- [ ] Benchmarks recorded; overhead within budget.
- [ ] README: 3-line setup, one screenshot-equivalent code block of a snapshot failure, one of a `verify` table, link to rules docs.
- [ ] GitHub Actions CI green on Linux and Windows, with tests executed on the real .NET 8 and .NET 10 runtimes (no roll-forward in CI).
