# Ombi validation results

Ran the original `PlexServerContentRepository`, `ExternalSqliteContext`, entity mappings and migrations from
[Ombi](https://github.com/Ombi-app/Ombi/tree/c56440986e06012fcb57b66c3b892b94b03040a5) on 2026-09-10. Commit:
`c56440986e06012fcb57b66c3b892b94b03040a5` (v4.60.16), EF Core 8.0.5, in-memory SQLite. The checkout remained unchanged.

**All four scenarios passed**, each run twice. Capture was complete in all eight scopes.

| Scope | Upstream call | Commands | Rows | Finding |
|---|---|---:|---:|---|
| `series-by-key` | `GetByKey` on an 8×13 series | 1 | 832 | **QS002 Error**, QS005 Info |
| `series-by-key-small` | the same call on a 2×3 series | 1 | 12 | QS005 Info |
| `all-episodes` | `GetAllEpisodes().CountAsync()` | 1 | 1 | none |
| `content-exists` | `ContentExists(imdbId)` | 1 | 1 | none |

## What was found

`PlexServerContentRepository.GetByKey` and `GetFirstContentByCustom` load a show with both of its collections in
one query (`PlexContentRepository.cs:97` and `:106`):

```csharp
await Db.PlexServerContent.Include(x => x.Seasons).Include(x => x.Episodes).FirstOrDefaultAsync(x => x.Key == key);
```

EF Core LEFT JOINs both collections into one SELECT, so the database returns one row per (season, episode)
combination. For a show with 8 seasons and 104 episodes that is **832 rows to load one entity** — every row
repeating the show's own columns and one season's. The multiplication is the seasons count, and it grows with
the show: a 12-season procedural with 250 episodes returns 3 000 rows for the same single object.

The query is also tracked (QS005): `GetByKey` is a read path, and the change tracker keeps an entry and a
snapshot for the show and for all 832 joined rows' entities.

## The same code path, silent at small data

`series-by-key-small` runs the identical query — same call site, same fingerprint `6e78aa202d5e` — against a
two-season show. 12 rows for one entity, and QS002 does not fire: the result is below the threshold where
multiplication matters. That is the rule behaving as designed, and it is also the reason this is worth
measuring rather than reviewing by eye. The problem is invisible in a test library and arrives with a
customer's actual one.

## Boundaries

Synthetic data: two shows, one library, SQLite. The harness calls the repository directly, not the Plex sync
job that uses it, so nothing here says how often that path runs or what it costs in a live server. QS012 is
silent for the same reason it is silent on Jellyfin — the harness declares no asynchronous host — although
Ombi's repository is `async` throughout and would not trigger it anyway. No latency claim: these queries take
under a millisecond against in-memory SQLite, which measures nothing about a real deployment.

[Machine-readable scope reports](ombi-reports.json).
[Harness and reproduction instructions](../../scripts/real-world/Ombi/README.md).
