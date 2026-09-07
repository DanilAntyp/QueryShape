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
- Known limitation: when context B executes a query whose SQL was first compiled with different non-SQL-affecting operators on context A
  (tracking vs. no-tracking, different tags via `TagWith` are visible in SQL so they are fine), the cached info from A is used.
  `IsTracking` may therefore be wrong for that command. Rules using `IsTracking` (QS005) treat it as a hint, not proof.
- Commands whose compilation fired on one thread and execution on another share nothing except the fingerprint cache; step 2 usually covers them.
