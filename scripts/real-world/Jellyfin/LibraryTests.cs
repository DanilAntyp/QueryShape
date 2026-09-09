using Emby.Server.Implementations.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using QueryShape;
using Xunit;
using Xunit.Abstractions;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

// Real upstream repository, entity mappings and SQLite provider; only the data and the wiring live here.
// The wiring mirrors upstream's own tests/Jellyfin.Server.Implementations.Tests/Item/SqliteDbTestFixture.cs.
public sealed class LibraryTests : IDisposable
{
    private const int MovieCount = 24;

    // Per-item fan-out. The defaults are modest; "library-page-rich" raises images and provider ids to what a
    // real library reaches (one image per image type, four metadata providers).
    private int _imagesPerMovie = 3;
    private int _providersPerMovie = 3;
    private int _linkedChildrenPerMovie = 1;

    private readonly ITestOutputHelper _output;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;
    private readonly IApplicationPaths _applicationPaths = new Mock<IApplicationPaths>().Object;
    private readonly QueryShapeOptions _options = new() { CaptureCallSites = true, ReadSourceFiles = true };
    private readonly ItemTypeLookup _itemTypeLookup = new();
    private readonly BaseItemRepository _repository;
    private readonly JellyfinUser _user = new("queryshape", "Default", "Default") { Id = Guid.Parse("10000000-0000-0000-0000-0000000000aa") };

    public LibraryTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(_connection)
            .UseQueryShape(_options)
            .Options;

        var serverConfigurationManager = new Mock<IServerConfigurationManager>();
        serverConfigurationManager.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        _repository = new BaseItemRepository(
            CreateDbContextFactory(),
            new Mock<IServerApplicationHost>().Object,
            _itemTypeLookup,
            serverConfigurationManager.Object,
            NullLogger<BaseItemRepository>.Instance);
    }

    [Theory]
    [InlineData("library-page")]
    [InlineData("library-page-rich")]
    [InlineData("library-page-random")]
    [InlineData("library-page-rich-random")]
    [InlineData("library-ids")]
    [InlineData("latest-movies")]
    [InlineData("genres")]
    public void Exercise(string scenario)
    {
        if (scenario is "library-page-rich" or "library-page-rich-random")
        {
            _imagesPerMovie = 10;
            _providersPerMovie = 4;
        }

        using (var context = CreateDbContext())
        {
            context.Database.EnsureCreated();
            Seed(context);
        }

        // Two runs: the second one sees EF's compiled-query cache warm, as a real second request would.
        for (var run = 1; run <= 2; run++)
        {
            using var scope = QueryShapeScope.Begin($"{scenario}/run-{run}", _options);
            switch (scenario)
            {
                case "library-page":
                case "library-page-rich":
                    var page = _repository.GetItems(LibraryPageQuery());
                    Assert.Equal(MovieCount, page.TotalRecordCount);
                    Assert.Equal(20, page.Items.Count);
                    break;
                case "library-page-random":
                case "library-page-rich-random":
                    var random = LibraryPageQuery();
                    random.OrderBy = [(ItemSortBy.Random, SortOrder.Ascending)];
                    Assert.Equal(20, _repository.GetItemList(random).Count);
                    break;
                case "library-ids":
                    Assert.Equal(MovieCount, _repository.GetItemIdsList(new InternalItemsQuery(_user)
                    {
                        IncludeItemTypes = [BaseItemKind.Movie],
                    }).Count);
                    break;
                case "latest-movies":
                    var latest = LibraryPageQuery();
                    latest.Limit = 12;
                    Assert.Equal(12, _repository.GetLatestItemList(latest, CollectionType.movies).Count);
                    break;
                case "genres":
                    var genres = _repository.GetGenres(new InternalItemsQuery(_user)
                    {
                        IncludeItemTypes = [BaseItemKind.Movie],
                        DtoOptions = new DtoOptions(true),
                    });
                    Assert.NotEmpty(genres.Items);
                    break;
            }

            Report(scope, scenario switch
            {
                "library-page" or "latest-movies" => ["QS006"],
                "library-page-rich" => ["QS002"],
                // Upstream's random-sort branch loads the same includes with AsSplitQuery; it must stay silent
                // even at the fan-out that makes the single-query path an error.
                "library-page-rich-random" => [],
                // The split-query path, the id-only query and the by-name path must stay silent.
                _ => [],
            });
        }
    }

    private InternalItemsQuery LibraryPageQuery() => new(_user)
    {
        IncludeItemTypes = [BaseItemKind.Movie],
        Limit = 20,
        StartIndex = 0,
        EnableTotalRecordCount = true,
        OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
        // What /Items asks for when a client renders a poster grid: every field, images and user data.
        DtoOptions = new DtoOptions(true),
    };

    private void Report(QueryShapeScope scope, string[] expectedRules)
    {
        _output.WriteLine($"--- {scope.Name}: {scope.CommandCount} commands, {scope.TotalCommandDuration.TotalMilliseconds:F1} ms");
        foreach (var command in scope.Commands)
        {
            _output.WriteLine($"  [{command.Sequence}] {command.Fingerprint} rows={command.RowsReturned?.ToString() ?? "-"} " +
                $"roots={command.DistinctRootsEstimate?.ToString() ?? "-"} {command.Duration.TotalMilliseconds:F1}ms " +
                $"{command.CallSite?.ToString() ?? "call site unknown"}");
            _output.WriteLine($"      {Truncate(command.Shape, 400)}");
        }

        var diagnoses = scope.Analyze();
        _output.WriteLine(diagnoses.Count == 0
            ? "  no diagnoses"
            : QueryShape.Reporting.DiagnosisFormatter.Format(diagnoses, "  "));
        Assert.NotEmpty(scope.Commands);
        Assert.All(scope.Commands, command => Assert.NotNull(command.Query));
        Assert.Equal(expectedRules, diagnoses.Select(d => d.RuleId).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + " …";

    private JellyfinDbContext CreateDbContext() => new(
        _dbOptions,
        NullLogger<JellyfinDbContext>.Instance,
        new SqliteDatabaseProvider(_applicationPaths, NullLogger<SqliteDatabaseProvider>.Instance),
        new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    private IDbContextFactory<JellyfinDbContext> CreateDbContextFactory()
    {
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateDbContext);
        return factory.Object;
    }

    // A small movie library: 24 movies in one library folder, each with images, provider ids, user data,
    // one linked alternate version, one genre mapping and a media stream, plus the Genre by-name items.
    private void Seed(JellyfinDbContext context)
    {
        var movieType = _itemTypeLookup.BaseItemKindNames[BaseItemKind.Movie]!;
        var libraryId = Guid.Parse("10000000-0000-0000-0000-0000000000f0");
        context.Users.Add(_user);
        var library = new BaseItemEntity
        {
            Id = libraryId,
            Type = _itemTypeLookup.BaseItemKindNames[BaseItemKind.CollectionFolder]!,
            Name = "Movies",
            IsFolder = true,
            IsVirtualItem = false,
        };
        context.BaseItems.Add(library);

        var genreNames = new[] { "Action", "Drama", "Comedy" };
        var genres = genreNames
            .Select(name => new ItemValue { ItemValueId = Guid.NewGuid(), Type = ItemValueType.Genre, Value = name, CleanValue = name.ToLowerInvariant() })
            .ToArray();
        context.ItemValues.AddRange(genres);

        // The by-name items the /Genres endpoint returns: a Genre BaseItem per genre, matched on CleanName.
        var genreType = _itemTypeLookup.BaseItemKindNames[BaseItemKind.Genre]!;
        for (var g = 0; g < genreNames.Length; g++)
        {
            context.BaseItems.Add(new BaseItemEntity
            {
                Id = Guid.Parse($"20000000-0000-0000-0000-{g + 1:d12}"),
                Type = genreType,
                Name = genreNames[g],
                SortName = genreNames[g],
                CleanName = genreNames[g].ToLowerInvariant(),
                PresentationUniqueKey = genreNames[g].ToLowerInvariant(),
                IsFolder = false,
                IsVirtualItem = false,
            });
        }

        var movies = new List<BaseItemEntity>();
        for (var i = 0; i < MovieCount; i++)
        {
            var id = Guid.Parse($"10000000-0000-0000-0000-{i + 1:d12}");
            var movie = new BaseItemEntity
            {
                Id = id,
                Type = movieType,
                Name = $"Movie {i:d2}",
                SortName = $"Movie {i:d2}",
                CleanName = $"movie {i:d2}",
                PresentationUniqueKey = $"movie-{i:d2}",
                MediaType = "Video",
                IsMovie = true,
                IsFolder = false,
                IsVirtualItem = false,
                TopParentId = libraryId,
                ParentId = libraryId,
                DateCreated = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                Path = $"/media/movies/Movie {i:d2}.mkv",
                RunTimeTicks = TimeSpan.FromMinutes(90 + i).Ticks,
            };
            movies.Add(movie);
            context.BaseItems.Add(movie);

            for (var image = 0; image < _imagesPerMovie; image++)
            {
                context.BaseItemImageInfos.Add(new BaseItemImageInfo
                {
                    Id = Guid.NewGuid(),
                    ItemId = id,
                    Item = movie,
                    Path = $"/media/movies/Movie {i:d2}-{image}.jpg",
                    ImageType = (ImageInfoImageType)(image % 10),
                    Width = 1000,
                    Height = 1500,
                });
            }

            foreach (var provider in new[] { "Imdb", "Tmdb", "Tvdb", "Omdb" }.Take(_providersPerMovie))
            {
                context.BaseItemProviders.Add(new BaseItemProvider
                {
                    ItemId = id,
                    Item = movie,
                    ProviderId = provider,
                    ProviderValue = $"{provider.ToLowerInvariant()}-{i}",
                });
            }

            context.UserData.Add(new UserData
            {
                ItemId = id,
                Item = movie,
                UserId = _user.Id,
                User = _user,
                CustomDataKey = $"movie-{i:d2}",
                PlayCount = i % 3,
                Played = i % 3 == 0,
                PlaybackPositionTicks = 0,
            });

            context.ItemValuesMap.Add(new ItemValueMap
            {
                ItemId = id,
                Item = movie,
                ItemValueId = genres[i % genres.Length].ItemValueId,
                ItemValue = genres[i % genres.Length],
            });

            context.MediaStreamInfos.Add(new MediaStreamInfo
            {
                ItemId = id,
                Item = movie,
                StreamIndex = 0,
                StreamType = MediaStreamTypeEntity.Video,
                Codec = "h264",
                Language = "eng",
                IsDefault = true,
                IsForced = false,
                IsExternal = false,
                IsInterlaced = false,
                IsAnamorphic = false,
                IsHearingImpaired = false,
            });

            context.AncestorIds.Add(new AncestorId { ItemId = id, Item = movie, ParentItemId = libraryId, ParentItem = library });
        }

        // Alternate versions, the way a library with multiple editions of a film looks.
        for (var i = 0; i < MovieCount; i++)
        {
            for (var child = 1; child <= _linkedChildrenPerMovie; child++)
            {
                context.LinkedChildren.Add(new LinkedChildEntity
                {
                    ParentId = movies[i].Id,
                    ChildId = movies[(i + child) % MovieCount].Id,
                    ChildType = Jellyfin.Database.Implementations.Entities.LinkedChildType.Manual,
                    SortOrder = child,
                });
            }
        }

        context.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();
}
