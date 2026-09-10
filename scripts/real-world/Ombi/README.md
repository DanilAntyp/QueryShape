# Ombi validation

Runs the original `PlexServerContentRepository`, `ExternalSqliteContext`, entity mappings and migrations from
[Ombi](https://github.com/Ombi-app/Ombi), pinned to `c56440986e06012fcb57b66c3b892b94b03040a5` (v4.60.16).

```sh
git clone https://github.com/Ombi-app/Ombi.git /tmp/queryshape-ombi-validation
git -C /tmp/queryshape-ombi-validation checkout c56440986e06012fcb57b66c3b892b94b03040a5
export OmbiRoot=/tmp/queryshape-ombi-validation   # macOS: /private/tmp/...
dotnet test scripts/real-world/Ombi/Ombi.Tests.csproj
```

Ombi targets `net8.0` and is SQLite-native, so nothing about its schema is adapted: the harness runs its real
migrations against an in-memory SQLite database. Locally the harness rolls forward to .NET 10 when no .NET 8
runtime is installed; CI runs it on the real one.

`ExternalSqliteContext` runs those migrations in its constructor behind a static "already created" flag, so a
second database in the same process would never get a schema. One `PlexLibrary` fixture therefore owns the
connection for the whole class and seeds both shows.

Fixture data is one long series (8 seasons, 13 episodes each) and one short one (2 seasons, 3 episodes each),
which is the shape a Plex library has.

| Scenario | Upstream call | Expected |
|---|---|---|
| `series-by-key` | `GetByKey` on the long series | QS002, QS005 |
| `series-by-key-small` | the same call on the short series | QS005 |
| `all-episodes` | `GetAllEpisodes().CountAsync()` | silent |
| `content-exists` | `ContentExists(imdbId)` | silent |

These tests cover repository queries against SQLite with synthetic data. They do not measure Plex sync as a
whole, real library sizes, other providers, or latency.
