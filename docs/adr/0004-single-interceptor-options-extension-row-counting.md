# ADR-0004: One interceptor instance, options as a DbContextOptions extension, exact row counts

Date: 2026-09-08. Status: accepted.

## Context
- `IQueryExpressionInterceptor` is an `ISingletonInterceptor`: EF Core includes the instance in its internal service-provider cache key.
  A new interceptor per `QueryShapeOptions` meant a new internal provider per options object; EF Core throws
  `ManyServiceProvidersCreatedWarning` after 20. Test suites create many options objects.
- `DataReaderClosingEventData.ReadCount` counts `Read()` calls, including the final one returning `false`, so a fully enumerated result of N rows
  reports N+1 while `First()` reports 1 for 1 row. The count cannot be corrected after the fact.
- EF Core already ships `TagWithCallSite()` (tag format `File: {path}:{line}`), so a QueryShape duplicate would only add API.

## Decision
- `QueryShapeInterceptor.Instance` is the only instance ever registered. `UseQueryShape(options)` stores the options in a
  `QueryShapeOptionsExtension` on the `DbContextOptions` (hash code 0, `ShouldUseSameServiceProvider = true`) and the interceptor resolves them
  per context through `IDbContextOptions`, cached in a weak table.
- `ReaderExecuted` returns a `CountingDataReader` wrapper that counts successful `Read()`s and reports on close/dispose.
  Overhead is one delegating virtual call per reader method; row counts are exact for every provider.
- QueryShape honours `TagWith` and EF Core's `TagWithCallSite`; tags are read from the expression tree (exact) with the SQL comment block as fallback,
  because EF Core joins several tags into one comment block. No QueryShape `TagWithCallSite` extension is provided.

## Consequences
- `services.AddQueryShape()` registers only `QueryShapeOptions`; `UseQueryShape()` without arguments finds it through the application service provider.
- The unscoped ring buffer and the correlation cache are process-wide (bounded).
