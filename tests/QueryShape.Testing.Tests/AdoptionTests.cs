using Microsoft.EntityFrameworkCore;
using QueryShape.Testing;

namespace QueryShape.Testing.Tests;

public class AdoptionTests
{
    [Fact]
    public void Local_differences_identify_fields_and_redact_entire_subtrees()
    {
        var before = new { Id = 1, Secret = new { Token = "old-secret" }, Name = "before" };
        var after = new { Id = 1, Secret = new { Token = "new-secret" }, Name = "after" };
        var safe = QueryBehavior.Compare(before, after);
        safe.Should().Contain(d => d.Path == "$[\"Name\"]" && d.Before == "[hidden]");
        var local = QueryBehavior.Compare(before, after, new BehaviorDiffOptions { IncludeValues = true, RedactPath = p => p == "$[\"Secret\"]" });
        local.Should().Contain(d => d.Path == "$[\"Name\"]" && d.After == "\"after\"");
        local.Should().Contain(d => d.Path == "$[\"Secret\"]" && d.After == "[redacted]");
        System.Text.Json.JsonSerializer.Serialize(local).Should().NotContain("old-secret").And.NotContain("new-secret");
    }

    [Fact]
    public async Task Named_cases_and_batch_boundaries_use_explicit_growth_budgets()
    {
        var scenario = new QueryScenario<int, ScenarioTests.Fixture, int>
        {
            Name = "batched",
            PrepareAsync = (n, _) => Task.FromResult(new ScenarioTests.Fixture(n)),
            ExecuteAsync = async (f, ct) =>
            {
                await using var db = f.Shop.CreateContext();
                var ids = await db.Customers.Select(c => c.Id).ToArrayAsync(ct);
                foreach (var batch in ids.Chunk(2)) await db.Orders.Where(o => batch.Contains(o.CustomerId)).CountAsync(ct);
                return ids.Length;
            },
            ObserveMetricsAsync = (_, _) => Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double> { ["fixture-metric"] = 7 }),
        };
        var report = await QueryScaling.RunCasesAsync(scenario,
            [new("empty", 0, 0), new("one-batch", 2, 2), new("next-batch", 3, 3)],
            new ScalingOptions { ConstantCommands = false, MaxCommandsForSize = n => 1 + (n + 1) / 2,
                ValidatePoint = p => p.Metrics["fixture-metric"] == 7 ? [] : ["external metric missing"] });
        report.AssertSatisfied();
        report.Points.Select(p => p.Commands).Should().Equal(1, 1, 2, 2, 3, 3);
        report.Points.Should().OnlyContain(p => p.ElapsedMs >= 0 && p.Complete);
    }

    [Fact]
    public void Relational_reduction_keeps_child_references_valid()
    {
        var input = new RelationalInput<int, (int Id, int Parent)>([1, 2, 3], [(1, 1), (2, 1), (3, 3)]);
        var candidates = RelationalReduction.RemoveParentGroups(input, p => p, c => c.Parent).ToArray();
        candidates.Should().NotBeEmpty();
        foreach (var candidate in candidates)
        {
            candidate.Parents.Count.Should().BeLessThan(input.Parents.Count);
            candidate.Children.All(c => candidate.Parents.Contains(c.Parent)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Equivalence_exposes_observation_coverage_and_opt_in_differences()
    {
        var result = await QueryEquivalence.CompareAsync(ScenarioTests.Customers(false), ScenarioTests.Customers(true),
            (IReadOnlyList<ScenarioTests.CustomerSeed>)[new(1, 0)], localDifferences: new BehaviorDiffOptions());
        result.Equivalent.Should().BeFalse();
        result.Coverage.Should().Contain("State: not observed");
        result.LocalDifferences.Should().NotBeEmpty();
        result.LocalDifferences.Should().OnlyContain(d => d.Before == "[hidden]" && d.After == "[hidden]");
    }
}
