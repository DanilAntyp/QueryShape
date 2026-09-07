# ADR-0006: Parameter names are canonicalized in shapes

Date: 2026-09-08. Status: accepted. Deviates from CLAUDE.md 4.3 ("keep parameter names"); CLAUDE.md updated.

## Context
EF Core derives SQL parameter names from C# identifiers: `@__customerId_0` in EF Core 8, `@customerId` in EF Core 10.
Keeping them in the shape means renaming a local variable changes the fingerprint and breaks every snapshot that contains the query,
and the same query has different fingerprints on EF Core 8 and 10.

## Decision
`SqlNormalizer` renames `@name` parameters positionally to `@p0`, `@p1`… in order of first appearance (`@@server_variables` are untouched).
Parameter *values* still never appear anywhere. The captured command keeps the real parameter names in `Parameters` for people who need them.

## Consequences
- Shapes and fingerprints are identical across EF Core 8 and 10 and survive variable renames.
- Two different queries that differ only in which variable feeds the same position still have different shapes if the SQL differs in any other way;
  if the SQL is otherwise identical they are, by definition, the same shape.
