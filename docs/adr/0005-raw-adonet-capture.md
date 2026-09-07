# ADR-0005: Raw ADO.NET capture is an opt-in connection wrapper

Date: 2026-09-08. Status: accepted.

## Context
Section 4.4 asks for a "DbConnection-level interceptor" so Dapper and hand-written commands are captured. EF Core's `IDbConnectionInterceptor`
sees EF's own open/close only; commands Dapper creates on the connection never pass through EF. Replacing the connection EF creates
(`ConnectionCreating`) with a wrapper would break providers that downcast (`NpgsqlConnection`, `SqlConnection` features such as bulk copy).

## Decision
- `FromSqlRaw`, `FromSqlInterpolated`, `ExecuteSqlRaw` and friends are captured by the command interceptor and marked `QuerySource.Raw`
  using EF Core's `CommandSource` (`FromSqlQuery`, `ExecuteSqlRaw`).
- For Dapper and raw `DbCommand` usage, `QueryShapeDbConnection` wraps any `DbConnection`; commands created from it are captured with
  `QuerySource.Raw`, parameters hashed, rows counted. Users opt in explicitly (`new QueryShapeDbConnection(connection, options)`).
- QueryShape never swaps EF Core's connection automatically.

## Consequences
- Zero risk to EF Core provider internals; Dapper users add one line where they create the connection.
- Raw commands have no `QueryInfo`; rules that need the expression tree skip them, count/shape/row-based rules include them.
