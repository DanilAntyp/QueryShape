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
        args.Add("minimal");
        var resultsDirectory = Path.Combine(reportDir, "test-results");
        args.Add("--logger");
        args.Add("trx;LogFilePrefix=queryshape");
        args.Add("--results-directory");
        args.Add(resultsDirectory);

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
        if (Directory.Exists(resultsDirectory))
        {
            var files = Directory.GetFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories);
            if (files.Length > 0)
            {
                try
                {
                    var identities = ReadTestIdentities(files);
                    result = result with { ExecutedTests = identities.Count, ExecutedTestIdentities = identities, ContractFailuresOnly = HasOnlyContractFailures(files) };
                }
                catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException) { /* Unknown execution coverage: verify refuses to approve it. */ }
            }
        }
        var metrics = RunMetrics.Load(reportDir);
        log.WriteLine($"  exit {result.ExitCode}, {metrics.Scopes} scope report(s), {metrics.Queries} queries");
        return (metrics, result);
    }

    internal static IReadOnlyList<string> ReadTestIdentities(IEnumerable<string> files) => files.SelectMany(file =>
        System.Xml.Linq.XDocument.Load(file).Descendants()
            .Where(e => e.Name.LocalName == "UnitTestResult" && (string?)e.Attribute("outcome") is "Passed" or "Failed")
            .Select(e =>
            {
                var id = (string?)e.Attribute("testId");
                var name = (string?)e.Attribute("testName");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("TRX results lack stable test identities.");
                return System.Text.Json.JsonSerializer.Serialize(new[] { id, name });
            }))
        .Order(StringComparer.Ordinal).ToArray();

    internal static bool HasOnlyContractFailures(IEnumerable<string> files)
    {
        var failures = files.SelectMany(file => System.Xml.Linq.XDocument.Load(file).Descendants()
            .Where(e => e.Name.LocalName == "UnitTestResult" && (string?)e.Attribute("outcome") == "Failed")).ToArray();
        return failures.Length > 0 && failures.All(e => e.Descendants().Any(m => m.Name.LocalName == "Message"
            && m.Value.Contains("QueryShape contract violated:", StringComparison.Ordinal)));
    }
}
