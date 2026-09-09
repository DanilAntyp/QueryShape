# Jellyfin validation results

Ran the original `BaseItemRepository`, `JellyfinDbContext` and entity mappings from
[Jellyfin](https://github.com/jellyfin/jellyfin/tree/cf09de60e4e5844ad181d7ef9019151c54969d44) on 2026-09-09.
Commit: `cf09de60e4e5844ad181d7ef9019151c54969d44`, EF Core 10.0.11, in-memory SQLite, .NET 10. The checkout
remained unchanged.

**All seven scenarios passed**, each run twice. Capture was complete in all 14 scopes. The same harness passed on the hosted Linux runner with the real .NET 10 runtime ([run 34411411094](https://github.com/DanilAntyp/QueryShape/actions/runs/34411411094)).

| Scope | Upstream call | Commands | Rows | Finding |
|---|---|---:|---:|---|
| `library-page` | `GetItems`, all fields | 2 | 181 | QS006 Warning |
| `library-page-rich` | the same, real per-item fan-out | 2 | 801 | **QS002 Error** |
| `library-page-random` | `GetItemList`, random sort (upstream's split path) | 7 | 200 | none |
| `library-page-rich-random` | the same at the rich fan-out | 7 | 360 | none |
| `library-ids` | `GetItemIdsList` | 1 | 24 | none |
| `latest-movies` | `GetLatestItemList(movies)` | 2 | 120 | QS006 Warning |
| `genres` | `GetGenres` | 4 | 9 | none |

## What was found

`BaseItemRepository.PrepareItemQuery` starts every item query with `AsSingleQuery()`
(`BaseItemRepository.QueryBuilding.cs:30`), and `ApplyNavigations`
(`BaseItemRepository.QueryBuilding.cs:249-302`) then adds up to seven collection `Include`s depending on the
`DtoOptions` the caller passes. For a poster-grid request — all fields, images and user data, which is what
`/Items` asks for — five of them apply: `Provider`, `LockedFields`, `UserData`, `Images`,
`LinkedChildEntities`. EF Core LEFT JOINs all five into one SELECT, so the row count is the product of the
collection sizes per item rather than their sum.

With 20 movies at three images and three provider ids each, that is **180 rows for 20 entities**
(`BaseItemRepository.Querying.cs:62`) — QS006, a warning: tolerable now, multiplicative later. Raising the
fan-out to what a real library reaches (one image per image type, four provider ids) takes the identical
query to **800 rows for the same 20 entities, 40 rows per item** — QS002, an error. `latest-movies` reaches
the same query through `LoadLatestByIds` (`BaseItemRepository.Querying.cs:216`): 108 rows for 12 entities.

The row multiplication is independent of the provider; the per-item fan-out, not the page size, is what
drives it.

Neither flagged query carries the explicit `AsSingleQuery()` that `PrepareItemQuery` set: on the collapsing
path `ApplyGroupingFilter` rebuilds the query from `context.BaseItems.AsNoTracking()`
(`QueryBuilding.cs:104-116`), and `LoadLatestByIds` builds its own query the same way, so both inherit the
context default instead. QueryShape reports them as `splitting: SingleQuery (context default)`, while the
queries that do keep the call — `GetLatestMovieItems` (`Querying.cs:248`) and `GetItemValues`
(`ByName.cs:222`, `:251`) — come back as `SingleQuery (explicit)`, and all six commands of the random-sort
path as `SplitQuery (explicit)`. The executed behavior is the same either way, because single-query loading
is also the default; what the rebuild drops is the statement of intent at the call site that pays for it.

## Measured alternative, using upstream's own code

Jellyfin already loads the same `ApplyNavigations` includes with `AsSplitQuery()` on its random-sort branch
(`BaseItemRepository.Querying.cs:92`). Pushing the rich fixture through that branch returns the same 20
items in **360 rows across 7 commands instead of 801 rows across 2** — QueryShape stays silent on it. This
is not a drop-in replacement: that branch also randomises order and issues an extra id query, and split
queries trade rows for round trips. It bounds the row cost of the alternative, nothing more.

QueryShape's suggested fix for both findings is `.AsSplitQuery()` on the item query, with a reconstructed
before/after LINQ tree and a unified diff against
`Jellyfin.Server.Implementations/Item/BaseItemRepository.Querying.cs`. Both are in
[the scope reports](jellyfin-reports.json).

## Known, suppressed upstream

EF Core raises `RelationalEventId.MultipleCollectionIncludeWarning` for exactly this pattern, and Jellyfin
ignores it globally in `SqliteDatabaseProvider.Initialise`
(`src/Jellyfin.Database/Jellyfin.Database.Providers.Sqlite/SqliteDatabaseProvider.cs:85-87`, marked TODO
pending [efcore#35873](https://github.com/dotnet/efcore/pull/35873)). So the pattern is deliberate and the
framework's own warning is off in production. What QueryShape adds is the measured multiplier per request
scope, the call site, and whether the splitting behavior was chosen there or inherited — not the news that
collection includes join. Whether to split these queries is an upstream judgement call about round trips
versus rows; this is not a bug report.

## No false positives in the silent scenarios

`library-page-random` and `library-page-rich-random` take the split path and stay silent; `library-ids`
(one projected query) and `genres` (by-name items plus counts) produce nothing. Notably QS003 did not fire
on the `AsEnumerable()` calls at `BaseItemRepository.Querying.cs:62` and `ByName.cs:212`, which are
deliberate deserialization boundaries after the database work, not client-side evaluation.

## Boundaries

Synthetic data: 24 movies, one library folder, fixed per-item fan-out. No HTTP endpoints, no real library
sizes or distributions, no provider execution plans, no latency claim — database time was under 1 ms per
scope on in-memory SQLite, which measures nothing about production. Seven scenarios on one repository do
not establish a false-positive or false-negative rate. Fixture ids avoid upstream's reserved
`PLACEHOLDER` item; query code and mappings are unchanged.

[Machine-readable scope reports](jellyfin-reports.json).
[Harness and reproduction instructions](../../scripts/real-world/Jellyfin/README.md).
