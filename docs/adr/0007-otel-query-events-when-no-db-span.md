# ADR-0007: Per-command data goes on the database span when there is one, otherwise as events on the ambient span

Date: 2026-09-08. Status: accepted.

## Context
Section 8 says: when a command executes, add `queryshape.*` tags to `Activity.Current`. With Npgsql or SqlClient tracing enabled, `Activity.Current`
during `ReaderExecuted` is the provider's command span, and tags are exactly right. With SQLite (no provider spans) or with provider tracing off,
`Activity.Current` is the ASP.NET Core request span; tagging it per command means the last command overwrites the previous ones.

## Decision
`QueryShapeOpenTelemetryListener` checks whether the ambient activity looks like a database span (`ActivityKind.Client` or a `db.system`/`db.system.name` tag):
- database span → set tags `queryshape.fingerprint`, `queryshape.shape` (≤ 1 KB), `queryshape.source`, `queryshape.callsite`, `queryshape.tags`;
- anything else → add one `queryshape.query` event carrying the same attributes plus `queryshape.duration_ms` (and `queryshape.rows` when known).
  `EmitQueryEvents = false` restores plain tagging.

Scope-level data is unchanged: `queryshape.diagnosis` events plus `queryshape.diagnosis_count`, `queryshape.max_severity` and `queryshape.query_count` tags on the span current when the scope ends (the request span under the middleware).
Unsampled activities receive only fingerprint and source. No activity is created unless `CreateActivitiesWhenNoneExist` is on **and** something listens to the `QueryShape` source.

## Consequences
- Nothing is lost when the provider emits no spans; nothing is duplicated when it does.
- Consumers filter traces by `queryshape.max_severity = Error` regardless of provider.

## Addendum 2026-09-08: the database span is tagged from the Executing side
The Context above assumed `Activity.Current` during `ReaderExecuted` is the provider's command span. It is not, for the two common setups:
EF Core raises its `CommandExecuted` diagnostic event (which `OpenTelemetry.Instrumentation.EntityFrameworkCore` uses to stop its span) *before*
it calls the `Executed` interceptor, and `Microsoft.Data.SqlClient` ends its own span when `ExecuteReader` returns. Only a provider that keeps its
span open until the reader closes (Npgsql) still had it current.

So the capturer now raises `IQueryShapeListener.OnCommandExecuting` from the `Executing` interceptors, with the shape, fingerprint, source, tags and
call site already resolved (that work is reused for the `Executed` capture, so nothing is done twice). The OpenTelemetry listener tags a database span
at that moment and remembers the command id; when the command finishes it skips the `queryshape.query` event for that id, so the request span is not
told twice. Rows and duration are unknown before execution, so a span tagged this way carries no `queryshape.rows`/`queryshape.duration_ms`; the
`queryshape.query.duration` histogram still records the duration. The `Executing` work only runs when at least one listener is registered.
