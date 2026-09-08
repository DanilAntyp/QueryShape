# Scaling, behavior preservation, and failure reduction

These APIs live in `QueryShape.Testing`, target EF Core 8 and 10, and work with any test framework. They require no LLM or additional runtime instrumentation. Use the same database provider as production when provider behavior matters. The executable [SQLite examples](../tests/QueryShape.Testing.Tests/ScenarioExamples.cs) and [integration tests](../tests/QueryShape.Testing.Tests/ScenarioTests.cs) show complete fixtures.

## A repeatable scenario

Create `QueryScenario<TInput, TFixture, TResult>` with a name, a fixture factory, and an operation. The fixture implements `IAsyncDisposable`. For example, using your own `ShopFixture` and result DTO:

```csharp
var scenario = new QueryScenario<int, ShopFixture, OrderDto[]>
{
    Name = "GetCustomerOrders",
    PrepareAsync = (size, ct) => ShopFixture.CreateAndSeedAsync(size, ct),
    ExecuteAsync = (fixture, ct) => fixture.Service.GetOrdersAsync(ct),
    ResultOrder = ResultOrder.Preserve,
    ObserveStateAsync = async (fixture, ct) =>
        await fixture.ReadStableDatabaseStateAsync(ct)
};
```

Each run calls the factory anew, observes optional initial state, captures the operation, observes final state, and disposes the fixture. Setup, state reads, and cleanup are excluded from command/row measurements. Fixtures must use `UseQueryShape()` and must be isolated from other runs. Do not invoke scenarios inside an ambient QueryShape scope. A factory that fails before returning must clean up resources it already allocated. Operation and observation failures propagate; they do not count as successful measurements.

Materialize query results inside `ExecuteAsync`. A returned `IQueryable` or lazy enumerable is not an observed result. Project to finite DTOs before returning. `NormalizeResult` can remove irrelevant timestamps or generated identifiers; it should be a pure in-memory projection. For state observations, read the persisted database through a fresh context or no-tracking queries, rather than relying on potentially stale tracked objects.

## Growth contracts

```csharp
var report = await QueryScaling.RunAsync(scenario, [10, 100, 1000],
    new ScalingOptions
    {
        Dimension = "customers",
        Repeats = 2,
        ConstantCommands = true,
        MaxCommands = 3,
        MaxRowsPerItem = 5
    });
output.WriteLine(report.ToText());
report.AssertSatisfied();
```

Available contracts: constant command count (default), constant rows, maximum commands, maximum rows, and maximum rows per input item. Every repetition is checked, so unstable counts cannot be hidden by a median. A row means one row read from a non-SaveChanges data reader: includes can multiply these rows, while an aggregate returns one row. Commands include reads, raw commands, and writes. Reader rows do not measure server-side rows scanned, bytes transferred, allocations, or query-plan cost. They are not a distinct entity count.

Disabled/overflowed capture, failed commands, incomplete readers and a completely uninstrumented run cannot pass. A scenario that has no commands at one size can be legitimate (e.g. a cache hit); a run with no commands at any size is rejected. Two repetitions also exercise compilation cache reuse with fresh fixtures; they are not separate process-level cold starts.

`scale --sizes 10,100,1000` overrides the sizes through `QUERYSHAPE_SIZES` in the child test process. The test should return the report without asserting a specific hardcoded size list. Call `AssertSatisfied()` for direct test gates or CLI runs. Its contract exception is recognized in TRX: available summaries are rendered and contract-only failures exit 1. Other test/setup failures exit 2 and still render available summaries. JSON includes `testRunStatus`.

Reports describe observed growth at supplied sizes, not a mathematical complexity bound or a prediction of production latency. Use a size-based scenario for one dimension, or named cases for multiple dimensions: customers, child rows per customer, basket size, or page size. Your seed factory determines distributions and business invariants.

## Behavior checks

For an existing test, explicitly record a stable observable value before disposing its scope:

```csharp
using var scope = QueryShapeScope.Begin("OrderService.GetOrders");
var result = await service.GetOrdersAsync();
scope.Observe("result", result);
```

Only a SHA-256 digest of canonical JSON is added to the report. Object property order is normalized. Array order is preserved by default. `ResultOrder.Ignore` sorts the root array for comparison while preserving duplicates and the order of nested arrays. Supply meaningful, stable observation names, unique within each scope. Normalization is explicit; the tool does not silently discard fields. Hashes are comparison artifacts, not anonymization for sensitive or easily guessed values.

`QueryScenario.RunAsync` records result and optional before/after state digests automatically in a separate zero-query behavior scope. `QueryEquivalence.CompareAsync(beforeScenario, afterScenario, input)` runs the two implementations independently and compares results, initial state, and final state. `comparison.AssertEquivalent()` fails on differences. This can catch a lost LEFT JOIN row or a no-tracking edit that no longer persists. It verifies the supplied observations on those inputs, not every possible execution or every database table.

`verify` and `fix --llm` now require passing tests, matching executed-test identities from TRX, matching scope multiplicities, complete captures, and matching explicit observations. Every patched repetition is checked. Missing observations block verification by default. `--performance-only` makes the absence explicit; observations supplied by the test are still compared. No configuration accepts failed tests as proof of a fix.

Keep functional assertions in the selected verification test. Assertions requiring an old query count, diagnosis, or SQL snapshot belong in separate tests: they are expected to change when queries are optimized. Do not update snapshots automatically during verification. A run with skipped tests only, no executed tests, or a runner that cannot provide TRX evidence cannot be approved.

## Reducing a failing dataset

```csharp
var reduced = await QueryReduction.MinimizeAsync(
    "missing-customer", originalData,
    candidates: QueryReduction.RemoveChunks,
    complexity: data => data.Count,
    evaluateAsync: async (candidate, ct) =>
    {
        if (!IsValid(candidate)) return ReductionTrial.Invalid;
        var check = await QueryEquivalence.CompareAsync(before, after, candidate, ct);
        return check.Equivalent ? ReductionTrial.Pass
            : ReductionTrial.Fail(string.Join(",", check.Differences));
    },
    options: new ReductionOptions
    {
        MaxAttempts = 200,
        Confirmations = 2,
        ReplayMethod = "MyTests.CustomerCases.StillFailsAsync"
    });
await reduced.WriteRegressionAsync("generated", "MyTests.CustomerCases.StillFailsAsync");
```

`RemoveChunks` deletes groups of list elements, then individual elements. For relational datasets, supply a candidate generator that preserves foreign keys, or return `Invalid` for invalid candidates. A candidate must have lower nonnegative complexity and reproduce exactly the same failure signature in every confirmation. Setup exceptions, other failures and cancellations are not accepted as the original failure. Use immutable inputs; evaluators must not mutate candidates.

`Complete` means no smaller reproducer was found among the candidates your generator offered from the final input. It does not mean globally smallest. The attempt budget includes initial confirmations and all actual trials; candidate enumeration is bounded too. If the budget expires, the last confirmed reproducer is returned with `Complete=false`. Unstable initial failures stop reduction with an error. This is not a full flakiness analysis.

### Generated tests

Implement the named replay helper in the target test project:

```csharp
public static async Task<bool> StillFailsAsync(string inputJson, CancellationToken ct)
{
    var input = JsonSerializer.Deserialize<CustomerSeed[]>(inputJson)!;
    var comparison = await QueryEquivalence.CompareAsync(before, after, input, ct);
    return !comparison.Equivalent && comparison.Differences.SequenceEqual(["result"]);
}
```

The generated `.cs` embeds the reduced JSON, calls this helper, and fails while the original failure persists. After the implementation is corrected, it passes. It uses xUnit by default; `RegressionFramework.NUnit` or `MSTest` generates the corresponding attributes. The target project must already reference that framework and contain the replay helper. The JSON companion is for inspection; the test does not depend on its working directory. New uniquely named files never overwrite existing tests.

No input values are exported unless artifact generation is explicitly requested. `reduce --out <directory>` requests it through `QUERYSHAPE_REDUCTION_DIR` and requires `ReductionOptions.ReplayMethod`. `--max-attempts` overrides the attempt budget. A completed reduction exits 0, an exhausted budget exits 1, and failed/invalid scenario execution exits 2. Add the generated source to your test project after reviewing the case; generating a test does not automatically include it in a project outside the output directory.

## Batch budgets, named distributions, and additional evidence

Set `ConstantCommands = false` and `MaxCommandsForSize = n => 1 + (n + 99) / 100` for a 100-item batch design. `MaxRowsForSize` supports a caller-defined row budget. Invalid negative budgets fail. `ValidatePoint` adds custom contracts over a point, its label, elapsed time and supplied metrics. These are explicit expectations, not inferred complexity classes.

`RunCasesAsync(scenario, cases, options)` accepts `ScalingCase<TInput>(name, size, input)` records. Names must be unique; sizes may be zero. Multiple cases can have the same size but different skew, nullability, tenant or relationship distributions. `--sizes` only overrides the integer `RunAsync` API, not named cases. Include empty, one-item, batch-boundary, missing-related-row and uneven-fanout cases that matter to the application.

`ScenarioRun.ElapsedMs` times the operation callback, excluding result hashing, state reads and fixture work. It is an in-process measurement, not production endpoint latency. `ObserveMetricsAsync` can collect caller-defined finite metrics after the operation outside query measurements, for example counters from a provider-specific plan capture you manage. Metric names and values appear in scaling summaries; do not put sensitive values in them. No plan query or destructive database operation is performed automatically.

## Observation coverage and local debugging

Set `StateDescription` to describe the persisted projection, such as `Orders: Id, CustomerId, Status, Total`. Reports record result normalization, array ordering and whether state was observed. `BehaviorComparison.Coverage` describes the checks; state is explicitly listed as not observed when absent. Recording a count alone does not detect wrong records; omitting a write-side table does not detect incorrect writes there.

```csharp
var comparison = await QueryEquivalence.CompareAsync(before, after, input,
    localDifferences: new BehaviorDiffOptions
    {
        IncludeValues = false, // paths and hidden values by default
        MaxDifferences = 20,
        RedactPath = path => path.Contains("Secret", StringComparison.Ordinal)
    });
foreach (var difference in comparison.LocalDifferences)
    output.WriteLine($"{difference.Path}: {difference.Before} -> {difference.After}");
```

`IncludeValues = true` explicitly allows raw values in this local result. Subtree redaction takes precedence. QueryShape does not add these values to scope reports. Exporting or logging the returned difference object is the caller's decision. `QueryBehavior.Compare(beforeValue, afterValue, options)` also works without database fixtures. Local array differences are positional; with unordered comparisons, reorder values for a readable local diff if necessary. Differences are bounded and are not a complete dump.

`RelationalReduction.RemoveParentGroups(input, parentKey, foreignKey)` shrinks a `RelationalInput<TParent,TChild>` by removing parents with their children. It validates the initial relationship. More complex graphs, child-only deletion, uniqueness and business constraints require a caller-owned generator. The generated regression test still needs the application's replay helper.
