using System.Text.Json;
using QueryShape.Testing;

namespace QueryShape.Testing.Tests;

/// <summary>Runnable CLI examples. The library returns reports; tests can additionally call AssertSatisfied to gate CI directly.</summary>
public class ScenarioExamples
{
    [Fact]
    public async Task Scale_customer_orders()
    {
        await QueryScaling.RunAsync(ScenarioTests.Growth(false), [10, 100, 1000], new ScalingOptions { Dimension = "customers" });
    }

    [Fact]
    public async Task Reduce_dropped_customer()
    {
        IReadOnlyList<ScenarioTests.CustomerSeed> input = Enumerable.Range(1, 10).Select(id => new ScenarioTests.CustomerSeed(id, id == 7 ? 0 : 1)).ToArray();
        await QueryReduction.MinimizeAsync("dropped-customer", input, QueryReduction.RemoveChunks, data => data.Count,
            EvaluateAsync, new ReductionOptions { ReplayMethod = "QueryShape.Testing.Tests.ScenarioExamples.DroppedCustomerStillFailsAsync" });
    }

    public static async Task<bool> DroppedCustomerStillFailsAsync(string inputJson, CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Deserialize<ScenarioTests.CustomerSeed[]>(inputJson) ?? throw new ArgumentException("Missing input", nameof(inputJson));
        return (await EvaluateAsync(input, cancellationToken)).Failure == "result";
    }

    private static async Task<ReductionTrial> EvaluateAsync(IReadOnlyList<ScenarioTests.CustomerSeed> input, CancellationToken ct)
    {
        if (input.Any(c => c.Orders < 0 || c.Id <= 0) || input.Select(c => c.Id).Distinct().Count() != input.Count) return ReductionTrial.Invalid;
        var comparison = await QueryEquivalence.CompareAsync(ScenarioTests.Customers(false), ScenarioTests.Customers(true), input, ct);
        return comparison.Equivalent ? ReductionTrial.Pass : ReductionTrial.Fail(string.Join(",", comparison.Differences));
    }
}
