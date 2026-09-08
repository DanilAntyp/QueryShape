# ADR-0009: Rule thresholds against false positives

Date: 2026-09-08. Status: accepted.

## Context
An audit of the rules against the sample app and the test model found four sources of noise:
- QS004 reported every query with no `Where` and no limit, including five-row lookup tables and `GroupBy` aggregates ("loads every Order row" for a query that returns groups).
- QS006 reported any two-collection `Include` whose result had more rows than roots, i.e. nearly every such query with data, at Warning level, which fails a default `[QueryBudget]`.
- QS005 reported keyless entity queries (never tracked by EF Core) and `Customers.Include(c => c.Orders)` when an included `Order` was edited and saved.
- QS009 grouped `TagWithCallSite` call sites by file (the tag has no member), so two unrelated tagged queries in one file looked like a loop body; QS001 and QS009 overlapped silently.

## Decision
- QS004's "no filter, no limit" branch is reported at Info instead of Warning while the result is below `UnboundedMinimumRows` (default 20): the finding stays visible, and a
  test database with ten rows still records it, but a lookup table no longer fails a budget or a snapshot gate. `GroupBy` queries are skipped by that branch. The row-count
  branch (`UnboundedRowThreshold`) is unchanged.
- QS006 requires `rows >= CartesianMinimumRows` and `rows >= 2 * roots`; when roots cannot be estimated, EF Core's `MultipleCollectionIncludeWarning` plus a non-tiny result suffices. The sample endpoint loads 15 customers so it still demonstrates the rule.
- The analyzer never marks a keyless entity type as tracked, and records `QueryInfo.IncludedEntityTypes`; QS005 treats a save of any loaded type (root or included) as a use of tracking.
- A call site parsed from `TagWithCallSite` has an empty `Member`; QS009 groups such sites by file and line, and its explanation names shapes that QS001 also reports.

## Consequences
- Section 5's table stays the contract; the thresholds above are the defaults it left open and are documented in each rule's page.
- Snapshots that recorded QS004 for lookup tables lose that entry on their next update (the committed DX snapshot was updated).
