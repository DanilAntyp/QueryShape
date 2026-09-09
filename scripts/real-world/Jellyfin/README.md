# Jellyfin validation

Runs the original `BaseItemRepository`, `JellyfinDbContext`, entity mappings and SQLite provider from
[Jellyfin](https://github.com/jellyfin/jellyfin), pinned to `cf09de60e4e5844ad181d7ef9019151c54969d44`.

```sh
git clone https://github.com/jellyfin/jellyfin.git /tmp/queryshape-jellyfin-validation
git -C /tmp/queryshape-jellyfin-validation checkout cf09de60e4e5844ad181d7ef9019151c54969d44
export JellyfinRoot=/tmp/queryshape-jellyfin-validation   # macOS: use /private/tmp/... (see below)
dotnet test -c Release scripts/real-world/Jellyfin/Jellyfin.Tests.csproj
```

`-c Release` is required: upstream references its own `Jellyfin.CodeAnalysis` analyzer only in `Debug`, and
that analyzer is built against a newer Roslyn than the .NET 10.0.301 SDK ships (`CS9057`).

On macOS pass the real path (`/private/tmp/...`), not `/tmp/...`. A symlinked root makes Roslyn compare the
upstream `.editorconfig` directory against differently-spelled source paths, the severities in it stop
applying, and upstream's own code then fails its StyleCop rules.

The wiring mirrors upstream's `tests/Jellyfin.Server.Implementations.Tests/Item/SqliteDbTestFixture.cs`:
in-memory SQLite, `NoLockBehavior`, the real `SqliteDatabaseProvider`, and `IServerApplicationHost` /
`IServerConfigurationManager` / `IApplicationPaths` mocked the same way. Only `UseQueryShape()` is added.
Upstream query code, mappings and `ApplyNavigations` are unchanged.

Fixture data is a 24-movie library in one `CollectionFolder`, each movie with images, provider ids, user
data, one linked alternate version, a genre mapping and a media stream, plus the three `Genre` by-name
items the `/Genres` endpoint returns. Fixture ids use a `1…`/`2…` prefix because
`00000000-0000-0000-0000-000000000001` is upstream's reserved `PLACEHOLDER` item, inserted by
`EnsureCreated`.

Scenarios, each run twice so the second run sees EF's compiled-query cache warm:

| Scenario | Upstream call | Expected |
|---|---|---|
| `library-page` | `GetItems`, all fields (what `/Items` asks for) | QS006 |
| `library-page-rich` | the same call with a real library's per-item fan-out (10 images, 4 provider ids) | QS002 |
| `library-page-random` | `GetItemList` with `ItemSortBy.Random`, upstream's `AsSplitQuery` path | silent |
| `library-ids` | `GetItemIdsList` | silent |
| `latest-movies` | `GetLatestItemList(CollectionType.movies)` | QS006 |
| `genres` | `GetGenres` (by-name items plus counts) | silent |

These tests cover repository queries against SQLite with synthetic data. They do not measure HTTP
endpoints, real library sizes or distributions, provider execution plans, or latency.
