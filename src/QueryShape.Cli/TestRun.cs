using QueryShape.Reporting;

namespace QueryShape.Cli;

/// <summary>Runs `dotnet test` with QueryShape reporting enabled and collects the metrics.</summary>
internal static class TestRun
{
    public static async Task<(RunMetrics Metrics, ProcessResult Process)> RunAsync(
        string repoRoot,
        string? project,
        string? filter,
        string reportDir,
        bool noBuild,
        IReadOnlyDictionary<string, string?>? extraEnvironment,
        TextWriter log,
        CancellationToken ct)
    {
        Directory.CreateDirectory(reportDir);
        var args = new List<string> { "test" };
        if (!string.IsNullOrEmpty(project))
        {
            args.Add(project);
        }

        if (!string.IsNullOrEmpty(filter))
        {
            args.Add("--filter");
            args.Add(filter);
        }

        if (noBuild)
        {
            args.Add("--no-build");
        }

        args.Add("--nologo");
        args.Add("-v");
        args.Add("q");

        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ScopeReport.DirectoryEnvironmentVariable] = reportDir,
            ["QUERYSHAPE_UPDATE_SNAPSHOTS"] = null,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        };
        if (extraEnvironment is not null)
        {
            foreach (var (k, v) in extraEnvironment)
            {
                env[k] = v;
            }
        }

        log.WriteLine($"  dotnet {string.Join(' ', args)}  (in {repoRoot})");
        var result = await ProcessRunner.RunAsync("dotnet", args, repoRoot, env, echo: null, ct);
        var metrics = RunMetrics.Load(reportDir);
        log.WriteLine($"  exit {result.ExitCode}, {metrics.Scopes} scope report(s), {metrics.Queries} queries");
        return (metrics, result);
    }
}
