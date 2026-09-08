using System.Data;
using Microsoft.EntityFrameworkCore.Diagnostics;
using QueryShape.Reporting;

namespace QueryShape.Testing;

/// <summary>A repeatable operation with a fresh disposable fixture for every input. Setup, state observation and disposal are excluded from measurements.</summary>
public sealed class QueryScenario<TInput, TFixture, TResult> where TFixture : IAsyncDisposable
{
    public required string Name { get; init; }
    public required Func<TInput, CancellationToken, Task<TFixture>> PrepareAsync { get; init; }
    public required Func<TFixture, CancellationToken, Task<TResult>> ExecuteAsync { get; init; }
    /// <summary>Optional stable projection of database state, read before and after execution. Normalize generated IDs/timestamps in this callback.</summary>
    public Func<TFixture, CancellationToken, Task<object?>>? ObserveStateAsync { get; init; }
    public Func<TResult, object?>? NormalizeResult { get; init; }
    public ResultOrder ResultOrder { get; init; }
    public string? StateDescription { get; init; }
    /// <summary>Optional caller-supplied metrics collected outside the measured operation (for example provider plan counters). No plan is inferred automatically.</summary>
    public Func<TFixture, CancellationToken, Task<IReadOnlyDictionary<string, double>>>? ObserveMetricsAsync { get; init; }
    public QueryShapeOptions CaptureOptions { get; init; } = new() { CaptureCallSites = true };

    public async Task<ScenarioRun<TResult>> RunAsync(TInput input, string? caseName = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (QueryShapeScope.Current is not null)
            throw new InvalidOperationException("Run scenarios outside an ambient QueryShape scope so setup and state reads are excluded.");
        cancellationToken.ThrowIfCancellationRequested();
        await using var fixture = await PrepareAsync(input, cancellationToken).ConfigureAwait(false);
        object? beforeObservation = ObserveStateAsync is null ? null : System.Text.Json.JsonSerializer.SerializeToElement(await ObserveStateAsync(fixture, cancellationToken).ConfigureAwait(false));
        var beforeState = ObserveStateAsync is null ? null : QueryBehavior.Digest(beforeObservation);
        TResult result;
        QueryShapeScope scope;
        string resultDigest;
        object? resultObservation;
        var stopwatch = new System.Diagnostics.Stopwatch();
        using (scope = QueryShapeScope.Begin(caseName ?? Name, CaptureOptions))
        {
            stopwatch.Start();
            result = await ExecuteAsync(fixture, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            resultObservation = System.Text.Json.JsonSerializer.SerializeToElement(NormalizeResult is null ? (object?)result : NormalizeResult(result));
            resultDigest = QueryBehavior.Digest(resultObservation, ResultOrder);
        }

        object? afterObservation = ObserveStateAsync is null ? null : System.Text.Json.JsonSerializer.SerializeToElement(await ObserveStateAsync(fixture, cancellationToken).ConfigureAwait(false));
        var afterState = ObserveStateAsync is null ? null : QueryBehavior.Digest(afterObservation);
        using (var behavior = QueryShapeScope.Begin((caseName ?? Name) + "/behavior", CaptureOptions))
        {
            behavior.Annotate("behavior.v1.result", resultDigest);
            if (beforeState is not null) behavior.Annotate("behavior.v1.state-before", beforeState);
            if (afterState is not null) behavior.Annotate("behavior.v1.state-after", afterState);
            behavior.Annotate("behavior.coverage.result", NormalizeResult is null ? "Serialized result" : "Caller-normalized result projection");
            behavior.Annotate("behavior.coverage.order", ResultOrder.ToString());
            behavior.Annotate("behavior.coverage.state", ObserveStateAsync is null ? "Not observed" : StateDescription ?? "Caller-supplied state projection; other state is not checked");
        }

        var metrics = ObserveMetricsAsync is null ? new Dictionary<string, double>() : await ObserveMetricsAsync(fixture, cancellationToken).ConfigureAwait(false);
        if (metrics.Any(m => string.IsNullOrWhiteSpace(m.Key) || !double.IsFinite(m.Value))) throw new InvalidOperationException("Metric names must be nonempty and values finite.");
        var commands = scope.Commands;
        var readers = commands.Where(c => c.ExecuteMethod == DbCommandMethod.ExecuteReader && c.Source != QuerySource.SaveChanges).ToArray();
        var complete = CaptureOptions.Enabled && !scope.Overflowed && !commands.Any(c => c.Failed)
            && readers.All(c => c.RowsReturned.HasValue);
        return new ScenarioRun<TResult>(result, resultDigest, beforeState, afterState,
            commands.Count, readers.Sum(c => (long)(c.RowsReturned ?? 0)), complete,
            ScopeReport.FromScope(scope, scope.Analyze()))
        {
            ElapsedMs = stopwatch.Elapsed.TotalMilliseconds,
            Metrics = metrics,
            ResultObservation = resultObservation,
            StateBeforeObservation = beforeObservation,
            StateAfterObservation = afterObservation,
        };
    }
}

/// <summary>Result values stay in the test process; exported scope reports contain only explicit behavior digests.</summary>
public sealed record ScenarioRun<TResult>(TResult Result, string ResultDigest, string? StateBeforeDigest, string? StateAfterDigest,
    int Commands, long Rows, bool Complete, ScopeReport Report)
{
    public double ElapsedMs { get; init; }
    public IReadOnlyDictionary<string, double> Metrics { get; init; } = new Dictionary<string, double>();
    internal object? ResultObservation { get; init; }
    internal object? StateBeforeObservation { get; init; }
    internal object? StateAfterObservation { get; init; }
}

/// <summary>Checks two implementations on independently prepared copies of the same input.</summary>
public static class QueryEquivalence
{
    public static async Task<BehaviorComparison> CompareAsync<TInput, TFixture, TResult>(
        QueryScenario<TInput, TFixture, TResult> before, QueryScenario<TInput, TFixture, TResult> after,
        TInput input, CancellationToken cancellationToken = default, BehaviorDiffOptions? localDifferences = null) where TFixture : IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var baseline = await before.RunAsync(input, before.Name + "/before", cancellationToken).ConfigureAwait(false);
        var candidate = await after.RunAsync(input, after.Name + "/after", cancellationToken).ConfigureAwait(false);
        var differences = new List<string>();
        if (!baseline.Complete || !candidate.Complete) differences.Add("capture-incomplete");
        if (baseline.StateBeforeDigest != candidate.StateBeforeDigest) differences.Add("initial-state");
        if (baseline.ResultDigest != candidate.ResultDigest) differences.Add("result");
        if (baseline.StateAfterDigest != candidate.StateAfterDigest) differences.Add("final-state");
        var details = new List<BehaviorDifference>();
        if (localDifferences is not null)
        {
            if (differences.Contains("result")) details.AddRange(BehaviorDiffer.Compare(baseline.ResultObservation, candidate.ResultObservation, localDifferences, "$.result"));
            if (differences.Contains("initial-state")) details.AddRange(BehaviorDiffer.Compare(baseline.StateBeforeObservation, candidate.StateBeforeObservation, localDifferences, "$.initialState"));
            if (differences.Contains("final-state")) details.AddRange(BehaviorDiffer.Compare(baseline.StateAfterObservation, candidate.StateAfterObservation, localDifferences, "$.finalState"));
        }
        return new BehaviorComparison(differences.Count == 0, differences, baseline.Commands, candidate.Commands, baseline.Rows, candidate.Rows)
        {
            LocalDifferences = details,
            Coverage = ["Result: " + (before.NormalizeResult is null ? "serialized result" : "normalized projection"),
                "Result order: " + before.ResultOrder,
                "State: " + (before.ObserveStateAsync is null ? "not observed" : before.StateDescription ?? "caller-supplied projection")],
        };
    }
}

public sealed record BehaviorComparison(bool Equivalent, IReadOnlyList<string> Differences, int BeforeCommands, int AfterCommands, long BeforeRows, long AfterRows)
{
    public IReadOnlyList<BehaviorDifference> LocalDifferences { get; init; } = [];
    public IReadOnlyList<string> Coverage { get; init; } = [];
    public void AssertEquivalent()
    {
        if (!Equivalent) throw new InvalidOperationException("QueryShape behavior changed: " + string.Join(", ", Differences));
    }
}
