# ADR-0002: Correlating LINQ expressions with SQL commands

Date: 2026-09-08. Status: accepted.

## Context
`IQueryExpressionInterceptor.QueryCompilationStarting` fires **only on a compiled-query-cache miss** and its `QueryExpressionEventData`
carries `Expression`, `ExpressionPrinter` and `Context` but no id that later appears on `CommandEventData` (verified against EF Core 10 source,
`src/EFCore/Diagnostics/QueryExpressionEventData.cs`). An N+1 loop compiles once and executes N times, so most commands have no compilation event at all.
Parameter extraction runs before the interceptor (`QueryCompiler.ExecuteCore` → `ExtractParameters`, then `QueryCompilationContext.CreateQueryExecutor`
calls `Logger.QueryCompilationStarting`), so the tree we see already has parameters in place of closure values.

## Decision
`ExpressionCorrelator` keeps, per `DbContext` instance (weak table):
- `Pending`: the most recent compilation not yet matched to a command;
- `Last`: the most recent compilation, matched or not.

And process-wide: a bounded `fingerprint → QueryInfo` cache (5 000 entries, cleared when full).

For each `LinqQuery`/`FromSqlQuery`/`ExecuteUpdate`/`ExecuteDelete` command:
1. If `Pending` exists and the SQL shape contains the root entity's table name, use it, remember `fingerprint → info`, clear `Pending`.
   If the table name is absent the compilation never executed (translation failure); it is dropped.
2. Else if the fingerprint is cached, use the cached info.
3. Else if `Last` matches the table name, use it (split queries: several commands for one compilation).
4. Else the command has no `QueryInfo`; rules that need the expression skip it.

## Consequences
- Same DbContext, same async flow: correct, including `Orders.ToList()` followed by `Orders.AsNoTracking().ToList()` (identical SQL, different tracking).
- Commands whose compilation fired on one thread and execution on another share nothing except the fingerprint cache; step 2 usually covers them.

## Addendum 2026-09-08: tracking is observed, not only inferred
The fingerprint cache cannot tell a tracked variant from an `AsNoTracking()` variant with the same SQL when a third context executes one of them
(EF Core's compiled-query cache means no compilation event fires). Two measures:
- `CapturedCommand.IsTracking` is `true`/`false` only when this context compiled the query itself (`Resolution.OwnCompilation`); for a cache hit it is `null`.
- QueryShape subscribes to `ChangeTracker.Tracked` once per context and counts entities that start being tracked *from a query* while a command's
  reader is open (`CapturedCommand.TrackedEntities`). Any count above zero sets `IsTracking = true`, whatever the cache said.

QS005 fires only when `IsTracking == true`. The residual ambiguity, a cache-resolved query whose entities were all already tracked, stays silent:
a false negative at Info level instead of a false positive. A per-query `AsNoTracking()` that overrides a tracking context is covered by the same rule.

## Addendum 2026-09-08: a pending compilation must fit the command
Step 1 used to accept any command whose SQL mentioned the root table. A compilation that never executes (`ToQueryString()`, a translation
that threw and was caught, an enumeration that was never started) therefore attached itself to the next cache-hit command on the same table
and poisoned the fingerprint cache with the wrong expression. Now:
- QueryShape subscribes to EF Core's `QueryExecutionPlanned` event (fired once translation succeeded). Once that event has been seen in the
  process, a compilation without it is one that failed and is never matched.
- The pending compilation is consumed by the next command whether or not it matches: a mismatch proves the compilation never ran.
- `Matches` checks necessary conditions instead of only the table name: a filter needs `WHERE`, a key filter needs a parameter, single-query
  collection includes need `JOIN`, a limit/aggregate needs `TOP`/`LIMIT`/`EXISTS`/`COUNT(`... and, when the expression has query parameters and
  the command has parameters, at least one parameter name must be shared (EF Core names SQL parameters after the expression's parameters).
The remaining ambiguity is a never-executed compilation that has the same table, structure and parameter names as the next command, i.e. the same query.
