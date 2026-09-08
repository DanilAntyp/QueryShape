using System.Text.Json;

namespace QueryShape.Cli.Commands;

internal sealed class SetupCommand
{
    public string? Project { get; init; }
    public string? TestFilter { get; init; }
    public string? ReportDirectory { get; init; }
    public bool Json { get; init; }

    public async Task<int> DoctorAsync(TextWriter output, TextWriter error, CancellationToken ct)
    {
        var checks = new List<SetupCheck>();
        RunMetrics metrics;
        if (ReportDirectory is not null) metrics = RunMetrics.Load(ReportDirectory);
        else
        {
            if (string.IsNullOrWhiteSpace(Project) || string.IsNullOrWhiteSpace(TestFilter))
            { error.WriteLine("doctor: select an existing integration test with --project and --test, or inspect --report-dir."); return 2; }
            var dir = Path.Combine(Path.GetTempPath(), "queryshape-doctor", Guid.NewGuid().ToString("N"));
            var (m, process) = await TestRun.RunAsync(Directory.GetCurrentDirectory(), Project, TestFilter, dir, false, null, error, ct);
            metrics = m;
            checks.Add(new("tests", process.Success && process.ExecutedTests > 0,
                process.Success && process.ExecutedTests > 0 ? $"Executed {process.ExecutedTests} tests." : "Check the filter, runtime and test runner. QueryShape requires dotnet test with TRX results."));
            if (!process.Success) error.WriteLine(process.StdOut + process.StdErr);
        }
        checks.Add(new("scopes", metrics.Scopes > 0, metrics.Scopes > 0 ? $"Captured {metrics.Scopes} scopes." : "Open QueryShapeScope.Begin around your existing operation. For TestServer enable PreserveExecutionContext."));
        checks.Add(new("commands", metrics.Queries > 0, metrics.Queries > 0 ? $"Captured {metrics.Queries} commands." : "Register UseQueryShape on the DbContext actually used by the test; exercise a database operation."));
        checks.Add(new("complete", metrics.Scopes > 0 && metrics.Reports.All(r => r.CaptureComplete == true), "Complete capture requires current instrumentation, enabled capture, no overflow, and materialized readers."));
        var providers = metrics.Reports.SelectMany(r => r.Providers).Distinct().ToArray();
        var observations = BehaviorVerification.Observations(metrics).Keys.ToArray();
        var result = new { passed = checks.All(c => c.Passed), checks, providers, observations,
            next = observations.Length == 0 ? "Record scope.Observe(\"result\", result) before using verify. Database state remains unchecked unless observed explicitly." : "Review observed projections; add state observations for writes, then introduce snapshot or scaling contracts." };
        if (Json) output.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        else
        {
            foreach (var check in checks) output.WriteLine($"{(check.Passed ? "PASS" : "FAIL")} {check.Name}: {check.Message}");
            output.WriteLine("Providers: " + string.Join(", ", providers));
            output.WriteLine(result.next);
        }
        return result.passed ? 0 : 2;
    }

    internal sealed record SetupCheck(string Name, bool Passed, string Message);

    internal static async Task<int> InitAsync(string path, string framework, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var attribute = framework switch { "nunit" => "[global::NUnit.Framework.Test]", "mstest" => "[global::Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]", _ => "[global::Xunit.Fact]" };
        var classAttribute = framework == "mstest" ? "[global::Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]" : "";
        var source = $$"""
            // Requires QueryShape.Testing and your test framework. Register UseQueryShape() on your test DbContext.
            // Replace RunExistingOperationAsync with your existing integration-test fixture/service call.
            using QueryShape;
            using QueryShape.Testing;
            namespace QueryShape.Generated;
            {{classAttribute}}
            public sealed class QueryShapeSmokeTests
            {
                {{attribute}}
                public async global::System.Threading.Tasks.Task Existing_operation_is_instrumented()
                {
                    using var scope = QueryShapeScope.Begin("existing-operation");
                    var result = await RunExistingOperationAsync();
                    if (scope.Commands.Count == 0)
                        throw new global::System.InvalidOperationException("No commands captured. Register UseQueryShape on the context used by this test.");
                    scope.Observe("result", result);
                    await scope.MatchSnapshotAsync();
                }

                private static global::System.Threading.Tasks.Task<object> RunExistingOperationAsync() =>
                    throw new global::System.NotImplementedException("Wire your existing database operation here; return a materialized DTO. Create and seed fixtures outside the measured scope.");
            }
            """;
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(source.AsMemory(), ct);
            output.WriteLine($"Created {path}. Wire the operation and its fixture, then run doctor --project <tests> --test Existing_operation_is_instrumented. Existing files and package references were not modified.");
            return 0;
        }
        catch (IOException ex) { error.WriteLine(ex.Message); return 2; }
    }
}
