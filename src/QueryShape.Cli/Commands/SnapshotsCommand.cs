namespace QueryShape.Cli.Commands;

/// <summary>`queryshape snapshots update`: rerun the tests with QUERYSHAPE_UPDATE_SNAPSHOTS=1.</summary>
internal sealed class SnapshotsUpdateCommand
{
    public string? Project { get; init; }

    public string? TestFilter { get; init; }

    public async Task<int> ExecuteAsync(TextWriter out_, TextWriter err, CancellationToken ct)
    {
        out_.WriteLine("QueryShape snapshots update: running tests with QUERYSHAPE_UPDATE_SNAPSHOTS=1");
        var dir = Path.Combine(Path.GetTempPath(), "queryshape-snapshots", Guid.NewGuid().ToString("N"));
        var env = new Dictionary<string, string?> { ["QUERYSHAPE_UPDATE_SNAPSHOTS"] = "1", ["CI"] = null };
        var (metrics, process) = await TestRun.RunAsync(Directory.GetCurrentDirectory(), Project, TestFilter, dir, noBuild: false, env, out_, ct);
        if (!process.Success)
        {
            err.WriteLine(process.StdOut);
            err.WriteLine(process.StdErr);
            err.WriteLine("snapshots update: dotnet test failed (exit code " + process.ExitCode + ").");
            return process.ExitCode;
        }

        out_.WriteLine($"done: {metrics.Scopes} scope(s) recorded; snapshots for the matched tests were rewritten. Review the changes under __querysnapshots__ with git diff.");
        return 0;
    }
}
