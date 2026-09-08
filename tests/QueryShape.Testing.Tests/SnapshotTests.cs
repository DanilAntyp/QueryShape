using Microsoft.EntityFrameworkCore;
using QueryShape.Core.Tests.TestModel;
using QueryShape.Testing;

namespace QueryShape.Testing.Tests;

public class SnapshotTests : IDisposable
{
    private readonly SqliteShop _shop = new(configure: o => o.UnboundedMinimumRows = 1);   // the five-product lookup table must stay a Warning here
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queryshape-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _shop.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static SnapshotOptions Local(Severity failOn = Severity.Warning)
        => new() { Environment = _ => null, FailOn = failOn, Log = _ => { } };

    private string PathFor(string name) => Path.Combine(_dir, "__querysnapshots__", $"SnapshotTests.{name}.json");

    private async Task RunQueriesAsync(QueryShapeScope scope, int customerId = 1, bool extra = false)
    {
        await using var ctx = _shop.CreateContext();
        await ctx.Customers.Where(c => c.Id == customerId).ToListAsync();
        await ctx.Products.CountAsync();
        if (extra)
        {
            await ctx.Orders.Where(o => o.CustomerId == customerId).ToListAsync();
        }
    }

    [Fact]
    public async Task First_run_creates_the_snapshot_and_passes()
    {
        var path = PathFor("first");
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await RunQueriesAsync(scope);

        var result = await scope.MatchSnapshotFileAsync(path, "SnapshotTests.first", Local());
        scope.Name.Should().Be("SnapshotTests.first", "an unnamed scope takes the test's name so reports do not say (unnamed)");

        result.Outcome.Should().Be(SnapshotOutcome.Created);
        result.Path.Should().Be(path);
        File.Exists(path).Should().BeTrue();
        var json = await File.ReadAllTextAsync(path);
        json.Should().StartWith("{\n  \"version\": 1,\n  \"queryCount\": 2,\n  \"queries\": [\n    {\n      \"fingerprint\": \"");
        json.Should().EndWith("\n");
        json.Should().NotContain("\r");
        json.Should().Contain("\"source\": \"Linq\"");
        json.Should().Contain("\"diagnostics\": [\n    {\n      \"ruleId\": \"QS005\",\n      \"severity\": \"Info\"\n    }\n  ]", "the tracked Customer query is Info-level known debt");
    }

    [Fact]
    public async Task Second_run_with_same_queries_matches_regardless_of_order_and_parameters()
    {
        var path = PathFor("match");
        using (var first = QueryShapeScope.Begin(options: _shop.Options))
        {
            await RunQueriesAsync(first, customerId: 1);
            await first.MatchSnapshotFileAsync(path, "t", Local());
        }

        using var second = QueryShapeScope.Begin(options: _shop.Options);
        await using (var ctx = _shop.CreateContext())
        {
            var otherCustomer = 7;
            await ctx.Products.CountAsync();
            await ctx.Customers.Where(c => c.Id == otherCustomer).ToListAsync();
        }

        var result = await second.MatchSnapshotFileAsync(path, "t", Local());
        result.Outcome.Should().Be(SnapshotOutcome.Matched);
    }

    [Fact]
    public async Task Added_query_fails_with_a_readable_diff()
    {
        var path = PathFor("added");
        using (var first = QueryShapeScope.Begin(options: _shop.Options))
        {
            await RunQueriesAsync(first);
            await first.MatchSnapshotFileAsync(path, "SnapshotTests.added", Local());
        }

        using var second = QueryShapeScope.Begin(options: _shop.Options);
        await RunQueriesAsync(second, extra: true);

        var act = () => second.MatchSnapshotFileAsync(path, "SnapshotTests.added", Local());
        var ex = (await act.Should().ThrowAsync<QuerySnapshotMismatchException>()).Which;

        ex.Comparison.Added.Should().ContainSingle().Which.ActualCount.Should().Be(1);
        ex.Comparison.Removed.Should().BeEmpty();
        ex.Message.Should().StartWith("QueryShape snapshot mismatch: SnapshotTests.added\n  snapshot: " + path + "\n\nQueries: 2 in snapshot, 3 now (+1)\n");
        ex.Message.Should().Contain("  + x1  ").And.Contain("  Linq  SELECT \"t0\".\"Id\", \"t0\".\"CustomerId\"");
        ex.Message.Should().Contain("        at SnapshotTests.cs:");
        ex.Message.Should().Contain("        linq: DbSet<Order>()");
        ex.Message.Should().EndWith("If this change is intended, update the snapshot: QUERYSHAPE_UPDATE_SNAPSHOTS=1 dotnet test, or `dotnet queryshape snapshots update`.\n");
        ex.Message.Should().NotContain("New diagnostics");
    }

    [Fact]
    public async Task Removed_query_and_count_change_are_reported()
    {
        var path = PathFor("removed");
        using (var first = QueryShapeScope.Begin(options: _shop.Options))
        {
            await RunQueriesAsync(first, extra: true);
            await using var ctx = _shop.CreateContext();
            await ctx.Products.CountAsync();
            await first.MatchSnapshotFileAsync(path, "t", Local());
        }

        using var second = QueryShapeScope.Begin(options: _shop.Options);
        await RunQueriesAsync(second);

        var act = () => second.MatchSnapshotFileAsync(path, "t", Local());
        var ex = (await act.Should().ThrowAsync<QuerySnapshotMismatchException>()).Which;
        ex.Message.Should().Contain("Queries: 4 in snapshot, 2 now (-2)\n");
        ex.Message.Should().Contain("  - x1  ").And.Contain("  - x2 -> x1  ");
        ex.Comparison.Removed.Should().HaveCount(2);
    }

    [Fact]
    public async Task New_diagnosis_at_or_above_fail_on_fails_and_known_diagnostics_do_not()
    {
        var path = PathFor("diag");
        var options = new QueryShapeOptions { CaptureCallSites = true, NPlusOneThreshold = 3, UnboundedMinimumRows = 1 };   // the five-product table must stay a Warning here

        // Snapshot taken with an unbounded query: QS004 Warning is recorded as known debt.
        using (var first = QueryShapeScope.Begin(options: options))
        {
            await using var ctx = _shop.CreateContext(options);
            await ctx.Products.ToListAsync();
            var result = await first.MatchSnapshotFileAsync(path, "t", Local());
            result.Snapshot.Diagnostics.Should().Equal(new SnapshotDiagnostic("QS004", "Warning"), new SnapshotDiagnostic("QS005", "Info"));
        }

        // Same query again: known diagnostic, no failure.
        using (var second = QueryShapeScope.Begin(options: options))
        {
            await using var ctx = _shop.CreateContext(options);
            await ctx.Products.ToListAsync();
            (await second.MatchSnapshotFileAsync(path, "t", Local())).Outcome.Should().Be(SnapshotOutcome.Matched);
        }

        // An N+1 appears: the query set changes and a new QS001 Error is reported with its fix.
        using var third = QueryShapeScope.Begin(options: options);
        await using (var ctx = _shop.CreateContext(options))
        {
            var products = await ctx.Products.ToListAsync();
            foreach (var p in products.Take(3))
            {
                await ctx.OrderLines.Where(l => l.ProductId == p.Id).ToListAsync();
            }
        }

        var act = () => third.MatchSnapshotFileAsync(path, "SnapshotTests.diag", Local());
        var ex = (await act.Should().ThrowAsync<QuerySnapshotMismatchException>()).Which;
        ex.Comparison.NewDiagnoses.Should().ContainSingle().Which.RuleId.Should().Be("QS001");
        ex.Comparison.KnownDiagnoses.Select(d => d.RuleId).Should().BeEquivalentTo(["QS004", "QS005", "QS005"], "QS004 and one QS005 are in the snapshot; the second QS005 (OrderLine) is Info, below FailOn");
        ex.Message.Should().Contain("\nNew diagnostics (severity >= Warning):\n  QS001 ERROR  N+1 query: OrderLine by ProductId executed 3 times at SnapshotTests.cs:");
        ex.Message.Should().Contain("    fix  Load all OrderLine rows in one query: collect the keys first, then .Where(ol =>",
            "Product has no navigation to OrderLines, so no Include can be suggested");
        ex.Message.Should().Contain("        linq: DbSet<OrderLine>() .Where(l => l.ProductId == ");
        ex.Message.Should().Contain("Known diagnostics already in the snapshot (not failing): QS004 Warning, QS005 Info x2\n");
    }

    [Fact]
    public async Task Fail_on_threshold_is_respected()
    {
        var path = PathFor("threshold");
        using (var first = QueryShapeScope.Begin(options: _shop.Options))
        {
            await RunQueriesAsync(first);
            await first.MatchSnapshotFileAsync(path, "t", Local());
        }

        // Same fingerprints, but now the unbounded Products query replaces the Count -> different query set, so use a fresh file per case.
        var path2 = PathFor("threshold2");
        using (var baseline = QueryShapeScope.Begin(options: _shop.Options))
        {
            await using var ctx = _shop.CreateContext();
            await ctx.Products.Where(p => p.Price > 0).ToListAsync();
            await baseline.MatchSnapshotFileAsync(path2, "t", Local());
        }

        // Duplicate the identical query: QS008 Warning appears but the multiset changes too; check severity gate with FailOn = Error
        // by making the comparison see only the diagnostics difference: same queries, new Warning.
        var path3 = PathFor("threshold3");
        var opts = new QueryShapeOptions { UnboundedRowThreshold = 100 };
        using (var s1 = QueryShapeScope.Begin(options: opts))
        {
            await using var ctx = _shop.CreateContext(opts);
            await ctx.Products.Where(p => p.Price > 0).ToListAsync();
            await s1.MatchSnapshotFileAsync(path3, "t", Local());
        }

        using var s2 = QueryShapeScope.Begin(options: new QueryShapeOptions { UnboundedRowThreshold = 1 });
        await using (var ctx = _shop.CreateContext(opts))
        {
            await ctx.Products.Where(p => p.Price > 0).ToListAsync();   // 5 rows > threshold 1 -> QS004 Warning, same query set
        }

        (await s2.MatchSnapshotFileAsync(path3, "t", Local(Severity.Error))).Outcome.Should().Be(SnapshotOutcome.Matched, "Warning is below FailOn = Error");
        var act = () => s2.MatchSnapshotFileAsync(path3, "t", Local(Severity.Warning));
        await act.Should().ThrowAsync<QuerySnapshotMismatchException>();
    }

    [Fact]
    public async Task Missing_snapshot_in_ci_mode_fails()
    {
        var path = PathFor("ci");
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await RunQueriesAsync(scope);

        var options = new SnapshotOptions { Environment = name => name == "CI" ? "true" : null, Log = _ => { } };
        var act = () => scope.MatchSnapshotFileAsync(path, "SnapshotTests.ci", options);
        var ex = (await act.Should().ThrowAsync<QuerySnapshotMissingException>()).Which;
        ex.SnapshotPath.Should().Be(path);
        ex.Message.Should().StartWith("QueryShape snapshot missing: SnapshotTests.ci\n").And.Contain("CI mode");
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task Update_variable_rewrites_the_snapshot_even_on_mismatch()
    {
        var path = PathFor("update");
        using (var first = QueryShapeScope.Begin(options: _shop.Options))
        {
            await RunQueriesAsync(first);
            await first.MatchSnapshotFileAsync(path, "t", Local());
        }

        using var second = QueryShapeScope.Begin(options: _shop.Options);
        await RunQueriesAsync(second, extra: true);

        var logged = new List<string>();
        var options = new SnapshotOptions { Environment = name => name == "QUERYSHAPE_UPDATE_SNAPSHOTS" ? "1" : null, Log = logged.Add };
        var result = await second.MatchSnapshotFileAsync(path, "SnapshotTests.update", options);

        result.Outcome.Should().Be(SnapshotOutcome.Updated);
        SnapshotSerializer.Deserialize(await File.ReadAllTextAsync(path)).QueryCount.Should().Be(3);
        logged.Should().ContainSingle().Which.Should().StartWith("QueryShape: snapshot updated for SnapshotTests.update");

        // and the next plain run matches
        using var third = QueryShapeScope.Begin(options: _shop.Options);
        await RunQueriesAsync(third, extra: true);
        (await third.MatchSnapshotFileAsync(path, "t", Local())).Outcome.Should().Be(SnapshotOutcome.Matched);
    }

    [Fact]
    public async Task Snapshot_outcome_is_annotated_on_the_scope_for_reports()
    {
        var path = PathFor("annotated");
        using (var first = QueryShapeScope.Begin(options: _shop.Options))
        {
            await RunQueriesAsync(first);
            await first.MatchSnapshotFileAsync(path, "SnapshotTests.annotated", Local());
            first.Annotations["snapshot.outcome"].Should().Be("created");
            first.Annotations["snapshot.test"].Should().Be("SnapshotTests.annotated");
        }

        using var second = QueryShapeScope.Begin(options: _shop.Options);
        await RunQueriesAsync(second, extra: true);
        var act = () => second.MatchSnapshotFileAsync(path, "SnapshotTests.annotated", Local());
        await act.Should().ThrowAsync<QuerySnapshotMismatchException>();

        second.Annotations["snapshot.outcome"].Should().Be("mismatch");
        second.Annotations["snapshot.expectedQueries"].Should().Be("2");
        second.Annotations["snapshot.added"].Should().Be("1");
        second.Annotations["snapshot.removed"].Should().Be("0");
        QueryShape.Reporting.ScopeReport.FromScope(second, second.Analyze()).Annotations!["snapshot.outcome"].Should().Be("mismatch");
    }

    [Fact]
    public void Serializer_round_trips_and_is_deterministic()
    {
        var snapshot = new QuerySnapshot
        {
            QueryCount = 2,
            Queries =
            [
                new SnapshotQuery("aaaaaaaaaaaa", "SELECT 1", "Linq", ["tag b", "tag a"]),
                new SnapshotQuery("bbbbbbbbbbbb", "SELECT \"x\" FROM \"T\"", "Raw", []),
            ],
            Diagnostics = [new SnapshotDiagnostic("QS004", "Warning")],
        };

        var json = SnapshotSerializer.Serialize(snapshot);
        json.Should().Be(
            "{\n  \"version\": 1,\n  \"queryCount\": 2,\n  \"queries\": [\n    {\n      \"fingerprint\": \"aaaaaaaaaaaa\",\n      \"shape\": \"SELECT 1\",\n      \"source\": \"Linq\",\n      \"tags\": [\n        \"tag b\",\n        \"tag a\"\n      ]\n    },\n    {\n      \"fingerprint\": \"bbbbbbbbbbbb\",\n      \"shape\": \"SELECT \\u0022x\\u0022 FROM \\u0022T\\u0022\",\n      \"source\": \"Raw\",\n      \"tags\": []\n    }\n  ],\n  \"diagnostics\": [\n    {\n      \"ruleId\": \"QS004\",\n      \"severity\": \"Warning\"\n    }\n  ]\n}\n");

        var back = SnapshotSerializer.Deserialize(json);
        SnapshotSerializer.Serialize(back).Should().Be(json);
        back.Queries[1].Shape.Should().Be("SELECT \"x\" FROM \"T\"");
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", "sqlite")]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "sqlserver")]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", "postgresql")]
    [InlineData("Pomelo.EntityFrameworkCore.MySql", "mysql")]
    [InlineData("Oracle.EntityFrameworkCore", "oracle")]
    [InlineData("Some.Vendor.Provider", "provider")]
    public void Provider_short_names(string provider, string expected)
        => SnapshotProvider.ShortName(provider).Should().Be(expected);

    [Fact]
    public async Task Caller_resolved_snapshots_get_the_provider_suffix_and_explicit_paths_do_not()
    {
        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        await RunQueriesAsync(scope);

        SnapshotProvider.SuffixFor(scope).Should().Be("sqlite");
        SnapshotProvider.WithSuffix("/x/__querysnapshots__/T.Test.json", "sqlite").Should().Be("/x/__querysnapshots__/T.Test.sqlite.json");

        var explicitPath = PathFor("explicit");
        (await scope.MatchSnapshotFileAsync(explicitPath, "t", Local())).Path.Should().Be(explicitPath, "explicit paths are used verbatim");

        var options = Local();
        options.DirectoryName = Path.Combine(_dir, "__querysnapshots__");
        var result = await scope.MatchSnapshotAsync(name: "suffixed", options: options);
        result.Path.Should().EndWith(Path.Combine("__querysnapshots__", "SnapshotTests.suffixed.sqlite.json"));
        result.Outcome.Should().Be(SnapshotOutcome.Created);
    }

    [Fact]
    public async Task A_snapshot_without_provider_suffix_is_still_honoured()
    {
        var options = Local();
        options.DirectoryName = Path.Combine(_dir, "__querysnapshots__");
        var logged = new List<string>();
        options.Log = logged.Add;

        using (var first = QueryShapeScope.Begin(options: _shop.Options))
        {
            await RunQueriesAsync(first);
            var created = await first.MatchSnapshotAsync(name: "legacy", options: options);
            // Simulate a snapshot from before provider suffixes.
            var legacy = created.Path.Replace(".sqlite.json", ".json", StringComparison.Ordinal);
            File.Move(created.Path, legacy);
        }

        using var second = QueryShapeScope.Begin(options: _shop.Options);
        await RunQueriesAsync(second);
        var result = await second.MatchSnapshotAsync(name: "legacy", options: options);

        result.Outcome.Should().Be(SnapshotOutcome.Matched);
        result.Path.Should().EndWith("SnapshotTests.legacy.json");
        logged.Should().ContainSingle(l => l.Contains("rename it to SnapshotTests.legacy.sqlite.json"));

        // Opting out of the suffix keeps the old naming.
        options.ProviderInFileName = false;
        (await second.MatchSnapshotAsync(name: "legacy", options: options)).Path.Should().EndWith("SnapshotTests.legacy.json");
    }

    [Fact]
    public void Resolve_path_puts_snapshots_next_to_the_test_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "repo", "tests");
        var path = QueryShapeScopeSnapshotExtensions.ResolvePath(Path.Combine(directory, "OrderServiceTests.cs"), "GetOrders_query_shape");
        path.Should().Be(Path.Combine(directory, "__querysnapshots__", "OrderServiceTests.GetOrders_query_shape.json"));
    }
}
