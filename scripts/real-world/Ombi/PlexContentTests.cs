using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ombi.Store.Context;
using Ombi.Store.Context.Sqlite;
using Ombi.Store.Entities;
using Ombi.Store.Repository;
using QueryShape;
using QueryShape.Reporting;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// One database for the whole class: <c>ExternalSqliteContext</c> runs Ombi's migrations in its constructor behind a static
/// "already created" flag, so a second database in the same process would never get a schema.
/// </summary>
public sealed class PlexLibrary : IDisposable
{
    public const string LongSeriesKey = "/library/metadata/1001";
    public const string ShortSeriesKey = "/library/metadata/1002";

    private readonly SqliteConnection _connection;

    public PlexLibrary()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        Options = new DbContextOptionsBuilder<ExternalSqliteContext>()
            .UseSqlite(_connection)
            .UseQueryShape(QueryShapeOptions)
            .Options;

        using var db = new ExternalSqliteContext(Options);   // Ombi migrates here
        Seed(db, LongSeriesKey, "A Long Series", seasons: 8, episodesPerSeason: 13, imdb: "tt1234567");
        Seed(db, ShortSeriesKey, "A Short Series", seasons: 2, episodesPerSeason: 3, imdb: "tt7654321");
    }

    public QueryShapeOptions QueryShapeOptions { get; } = new() { CaptureCallSites = true, ReadSourceFiles = true };

    public DbContextOptions<ExternalSqliteContext> Options { get; }

    public void Dispose() => _connection.Dispose();

    private static void Seed(ExternalSqliteContext db, string key, string title, int seasons, int episodesPerSeason, string imdb)
    {
        var series = new PlexServerContent
        {
            Title = title,
            Key = key,
            ImdbId = imdb,
            TheMovieDbId = "1234",
            TvDbId = "4321",
            Type = MediaType.Series,
            Url = "https://example.invalid" + key,
            Quality = "1080",
            AddedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Seasons = Enumerable.Range(1, seasons)
                .Select(s => new PlexSeasonsContent { SeasonNumber = s, SeasonKey = key + "/season/" + s, ParentKey = key })
                .ToList(),
        };
        db.PlexServerContent.Add(series);
        db.SaveChanges();

        foreach (var season in Enumerable.Range(1, seasons))
        {
            for (var episode = 1; episode <= episodesPerSeason; episode++)
            {
                db.PlexEpisode.Add(new PlexEpisode
                {
                    Key = $"{key}/episode/{season:d2}{episode:d2}",
                    SeasonNumber = season,
                    EpisodeNumber = episode,
                    Title = $"S{season:d2}E{episode:d2}",
                    ParentKey = key + "/season/" + season,
                    GrandparentKey = key,   // the FK: PlexEpisode.GrandparentKey -> PlexServerContent.Key
                });
            }
        }

        db.SaveChanges();
    }
}

// Ombi's own repository, context, entity mappings and migrations; only the data and the wiring live here.
public sealed class PlexContentTests(ITestOutputHelper output, PlexLibrary library) : IClassFixture<PlexLibrary>
{
    private readonly ITestOutputHelper _output = output;
    private readonly QueryShapeOptions _options = library.QueryShapeOptions;

    [Theory]
    // A show with the shape a real library has: seasons, and every season's episodes.
    [InlineData("series-by-key")]
    [InlineData("series-by-key-small")]
    [InlineData("all-episodes")]
    [InlineData("content-exists")]
    public async Task Exercise(string scenario)
    {
        // Two runs: the second sees EF's compiled-query cache warm, as a second request would.
        for (var run = 1; run <= 2; run++)
        {
            await using var db = new ExternalSqliteContext(library.Options);
            var repository = new PlexServerContentRepository(db);
            using var scope = QueryShapeScope.Begin($"{scenario}/run-{run}", _options);

            switch (scenario)
            {
                case "series-by-key":
                    var series = await repository.GetByKey(PlexLibrary.LongSeriesKey);
                    Assert.NotNull(series);
                    Assert.Equal(8, series!.Seasons.Count);
                    Assert.Equal(8 * 13, series.Episodes.Count);
                    break;
                case "series-by-key-small":
                    var shortSeries = await repository.GetByKey(PlexLibrary.ShortSeriesKey);
                    Assert.NotNull(shortSeries);
                    Assert.Equal(2 * 3, shortSeries!.Episodes.Count);
                    break;
                case "all-episodes":
                    Assert.Equal(8 * 13 + 2 * 3, await repository.GetAllEpisodes().CountAsync());
                    break;
                case "content-exists":
                    Assert.True(await repository.ContentExists("tt1234567"));
                    break;
            }

            Report(scope, scenario switch
            {
                // The repository tracks what it reads, and the long series multiplies seasons by episodes.
                "series-by-key" => ["QS002", "QS005"],
                // The same query and the same fingerprint on a two-season show: too few rows to call it an explosion.
                "series-by-key-small" => ["QS005"],
                // A count over one table, and an existence check: nothing to report.
                _ => [],
            });
        }
    }

    private void Report(QueryShapeScope scope, string[] expectedRules)
    {
        _output.WriteLine($"--- {scope.Name}: {scope.CommandCount} commands, {scope.TotalCommandDuration.TotalMilliseconds:F1} ms");
        foreach (var command in scope.Commands)
        {
            _output.WriteLine($"  [{command.Sequence}] {command.Fingerprint} rows={command.RowsReturned?.ToString() ?? "-"} " +
                $"roots={command.DistinctRootsEstimate?.ToString() ?? "-"} {command.CallSite?.ToString() ?? "call site unknown"}");
        }

        var diagnoses = scope.Analyze();
        _output.WriteLine(diagnoses.Count == 0 ? "  no findings" : DiagnosisFormatter.Summarize(diagnoses, "  ") + "\n" + DiagnosisFormatter.Format(diagnoses, "  "));

        Assert.NotEmpty(scope.Commands);
        Assert.Equal(expectedRules, diagnoses.Select(d => d.RuleId).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

}
