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
