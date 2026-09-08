using System.Text.Json;
using Clean.Architecture.Core.ContributorAggregate;
using Clean.Architecture.Infrastructure.Data;
using Clean.Architecture.Infrastructure.Data.Queries;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QueryShape;
using QueryShape.Testing;
using Xunit;
using Xunit.Abstractions;

public class ContributorTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Ordered_projected_page_has_bounded_returned_rows()
    {
        var scenario = new QueryScenario<int, Fixture, object>
        {
            Name = "clean-contributor-page",
            PrepareAsync = (n, ct) => Fixture.CreateAsync(Enumerable.Range(1, n).Select(i => new Seed(i, i % 2 == 0)).ToArray(), ct),
            ExecuteAsync = async (f, ct) =>
            {
                var page = await new ListContributorsQueryService(f.Db).ListAsync(1, 4);
                Assert.Equal(Math.Min(4, f.Count), page.Items.Count);
                Assert.Equal(f.Count, page.TotalCount);
                return Projection(page.Items);
            },
        };
        var report = await QueryScaling.RunAsync(scenario, [0, 1, 10, 100],
            new ScalingOptions { MaxCommands = 2, MaxRowsForSize = n => Math.Min(n, 4) + 1 });
        output.WriteLine(report.ToText());
        report.AssertSatisfied();
    }

    [Fact]
    public async Task Reduce_a_result_regression_in_a_candidate_using_the_real_service()
    {
        // Negative control: the upstream service correctly returns contributors without phones.
        // Only the test's proposed candidate filters them out; this is not an upstream bug claim.
        IReadOnlyList<Seed> data = Enumerable.Range(1, 12).Select(i => new Seed(i, i != 7)).ToArray();
        var result = await QueryReduction.MinimizeAsync("clean-dropped-phone-less-contributor", data,
            QueryReduction.RemoveChunks, rows => rows.Count, EvaluateAsync,
            new ReductionOptions { ReplayMethod = "ContributorTests.StillDropsContributorAsync" });
        output.WriteLine(result.Summary.ToText());
        Assert.True(result.Summary.Complete);
        Assert.Single(result.Input);
        Assert.False(result.Input[0].HasPhone);
    }

    public static async Task<bool> StillDropsContributorAsync(string json, CancellationToken ct) =>
        (await EvaluateAsync(JsonSerializer.Deserialize<Seed[]>(json)!, ct)).Failure == "result";

    private static async Task<ReductionTrial> EvaluateAsync(IReadOnlyList<Seed> data, CancellationToken ct)
    {
        var before = Scenario(false);
        var after = Scenario(true);
        var comparison = await QueryEquivalence.CompareAsync(before, after, data, ct, new BehaviorDiffOptions());
        Assert.DoesNotContain("initial-state", comparison.Differences);
        return comparison.Equivalent ? ReductionTrial.Pass : ReductionTrial.Fail(string.Join(",", comparison.Differences));
    }

    private static QueryScenario<IReadOnlyList<Seed>, Fixture, object> Scenario(bool dropMissing) => new()
    {
        Name = "clean-contributor-results",
        PrepareAsync = Fixture.CreateAsync,
        ExecuteAsync = async (f, ct) =>
        {
            var page = await new ListContributorsQueryService(f.Db).ListAsync(1, Math.Max(1, f.Count));
            return Projection(dropMissing ? page.Items.Where(c => !string.IsNullOrEmpty(c.PhoneNumber.Number)) : page.Items);
        },
        ObserveStateAsync = async (f, ct) => (await f.Db.Contributors.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct))
            .Select(c => new { Id = c.Id.Value, Name = c.Name.Value, Phone = c.PhoneNumber?.Number }).ToArray(),
        StateDescription = "Contributors: Id, Name, PhoneNumber.Number",
    };

    private static object Projection(IEnumerable<Clean.Architecture.UseCases.Contributors.ContributorDto> items) =>
        items.Select(c => new { Id = c.Id.Value, Name = c.Name.Value, Phone = c.PhoneNumber.Number }).ToArray();

    public sealed record Seed(int Id, bool HasPhone);

    private sealed class Fixture(SqliteConnection connection, AppDbContext db, int count) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public int Count { get; } = count;
        public static async Task<Fixture> CreateAsync(IReadOnlyList<Seed> rows, CancellationToken ct)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(ct);
            try
            {
                var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).UseQueryShape().Options;
                await using (var seed = new AppDbContext(options))
                {
                    await seed.Database.EnsureCreatedAsync(ct);
                    foreach (var row in rows)
                    {
                        var contributor = new Contributor(ContributorName.From("Contributor " + row.Id));
                        // Deterministic positive fixture IDs avoid temporary negative IDs rejected by the upstream value object.
                        seed.Entry(contributor).Property(c => c.Id).CurrentValue = ContributorId.From(row.Id);
                        if (row.HasPhone) contributor.UpdatePhoneNumber(new PhoneNumber("48", "123456", null));
                        seed.Add(contributor);
                    }
                    await seed.SaveChangesAsync(ct);
                }
                return new(connection, new AppDbContext(options), rows.Count);
            }
            catch { await connection.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
