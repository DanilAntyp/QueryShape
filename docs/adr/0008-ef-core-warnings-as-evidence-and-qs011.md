# ADR-0008: EF Core's compile-time warnings as evidence; rule QS011 added

Date: 2026-09-08. Status: accepted. Adds a rule beyond the section 5 table, approved by the repository owner.

## Context
EF Core already computes several query-shape warnings at compile time: `RowLimitingOperationWithoutOrderByWarning`, `FirstWithoutOrderByAndFilterWarning`,
`DistinctAfterOrderByWithoutRowLimitingOperatorWarning` (core) and `MultipleCollectionIncludeWarning` (relational). They go to `ILogger` and to the
`Microsoft.EntityFrameworkCore` `DiagnosticSource`. Nobody reads them in production logs.

## Decision
- `EfCoreWarningObserver` subscribes once per process to EF Core's DiagnosticSource for exactly those event names. EF Core only materializes the event data
  when a listener asks for that name, so the cost is limited to the compilations that actually warn.
- A warning is attached to the query whose compilation is running on the current async flow (an `AsyncLocal` set by `QueryCompilationStarting`;
  EF Core compiles synchronously on the calling flow), falling back to the event's `DbContext` (`Pending ?? Last`) and finally to the latest compilation anywhere.
  The fallback matters for events without context data such as `MultipleCollectionIncludeWarning`.
- New rule **QS011 Row limiting without OrderBy** (Warning) reports paging and `First` without an order, with an `OrderBy(x => x.Key)` fix and a patch.
- QS006 also fires when EF Core warned about multiple collection includes and the result has at least `CartesianMinimumRows` rows, even when the
  root-cardinality estimate could not see the multiplication.

## Consequences
- Warnings ignored through `ConfigureWarnings(w => w.Ignore(...))` are not raised and therefore not seen; that is the user's explicit choice.
- Users who `Throw` on these warnings never reach QueryShape: the query fails at compile time instead.
