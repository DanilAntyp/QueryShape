# ADR-0003: How QS003 detects client-side evaluation

Date: 2026-09-08. Status: accepted.

## Context
Section 5 describes two signals: (a) nodes EF Core could not translate, detected via `QueryCompilationStarting` plus provider warnings, and
(b) `AsEnumerable()`/`ToList()` before `Where`/`Select`/`OrderBy`.

Findings against EF Core 8 and 10:
- `RelationalEventId.QueryClientEvaluationWarning` still has an id but has not been raised since EF Core 3.0. There is no event for
  "client evaluation happened in the final projection"; it is simply how the shaper works.
- Untranslatable calls anywhere except the final `Select` throw at compile time, so the query never produces a command.
- Operators applied after `AsEnumerable()`/`ToList()` are LINQ-to-Objects: they are not part of the expression EF Core compiles and are invisible to any interceptor.

## Decision
QS003 works on the pre-translation tree. A `MethodCallExpression` (or delegate `InvocationExpression`) inside an operator lambda is reported as
client-evaluated when its declaring type is **not** in a namespace EF Core or a provider can translate (`System*`, `Microsoft.*`, `Npgsql*`,
`Pomelo*`, `Oracle*`, `NetTopologySuite*`) **and** it is not mapped with `HasDbFunction`. Because parameter extraction has already replaced
closure-only subtrees, any remaining user call depends on row data and will run per row.

Signal (b) is not detectable at runtime. What it produces, an unfiltered query that loads everything so that LINQ-to-Objects can filter it,
is reported by QS004 (unbounded result set). This is documented in `docs/rules/QS003.md`.

## Consequences
- No false positives from EF-translatable functions; possible false negatives for user methods living in a `System.*` or `Microsoft.*` namespace.
- A Roslyn analyzer would be the right tool for (b); it is out of scope for v0.1 and would need its own ADR.

## Addendum 2026-09-08
Signal (b) now has its compile-time check: `QueryShape.Analyzers` ships QSA001, which flags a reducing operator applied directly to
`ToList()`/`ToArray()`/`ToListAsync()`/`ToArrayAsync()` on an `IQueryable`. Deliberately narrow: direct chains only, `AsEnumerable()` never flagged,
`Select` not flagged. Separate package, separate release cadence.
