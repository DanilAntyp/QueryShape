using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace QueryShape.Testing;

/// <summary><c>await scope.MatchSnapshotAsync();</c> — compare the queries this scope captured against <c>__querysnapshots__/{TestClass}.{TestName}.{provider}.json</c>.</summary>
public static class QueryShapeScopeSnapshotExtensions
{
    /// <summary>
    /// Compares the scope against its snapshot. Missing snapshot: written and the test passes (fails in CI mode). Mismatch: throws
    /// <see cref="QuerySnapshotMismatchException"/> with a readable diff. Test class is the caller's file name, test name the caller's member name;
    /// pass <paramref name="name"/> to override the test name. The provider goes into the file name (see <see cref="SnapshotOptions.ProviderInFileName"/>).
    /// </summary>
    public static Task<SnapshotResult> MatchSnapshotAsync(
        this QueryShapeScope scope,
        string? name = null,
        SnapshotOptions? options = null,
        [CallerFilePath] string callerFilePath = "",
        [CallerMemberName] string callerMemberName = "")
        => SnapshotEngine.MatchAsync(scope, ResolvePath(callerFilePath, name ?? callerMemberName, options), $"{Path.GetFileNameWithoutExtension(callerFilePath)}.{name ?? callerMemberName}", options ?? SnapshotOptions.Default, applyProviderSuffix: true);

    /// <summary>Synchronous variant of <see cref="MatchSnapshotAsync"/>.</summary>
    public static SnapshotResult MatchSnapshot(
        this QueryShapeScope scope,
        string? name = null,
        SnapshotOptions? options = null,
        [CallerFilePath] string callerFilePath = "",
        [CallerMemberName] string callerMemberName = "")
        => SnapshotEngine.MatchAsync(scope, ResolvePath(callerFilePath, name ?? callerMemberName, options), $"{Path.GetFileNameWithoutExtension(callerFilePath)}.{name ?? callerMemberName}", options ?? SnapshotOptions.Default, applyProviderSuffix: true)
            .GetAwaiter().GetResult();

    /// <summary>Compares against an explicit snapshot file path, used verbatim (for adapters and tools).</summary>
    public static Task<SnapshotResult> MatchSnapshotFileAsync(this QueryShapeScope scope, string snapshotPath, string testName, SnapshotOptions? options = null)
        => SnapshotEngine.MatchAsync(scope, snapshotPath, testName, options ?? SnapshotOptions.Default, applyProviderSuffix: false);

    /// <summary>The snapshot path for a test source file and test name.</summary>
    public static string ResolvePath(string callerFilePath, string testName, SnapshotOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(callerFilePath);
        ArgumentException.ThrowIfNullOrEmpty(testName);
        var dir = Path.GetDirectoryName(callerFilePath) ?? ".";
        var testClass = Path.GetFileNameWithoutExtension(callerFilePath);
        var safe = string.Concat((testClass + "." + testName).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return Path.Combine(dir, (options ?? SnapshotOptions.Default).DirectoryName, safe + ".json");
    }
}

internal static class SnapshotEngine
{
    public static async Task<SnapshotResult> MatchAsync(QueryShapeScope scope, string basePath, string testName, SnapshotOptions options, bool applyProviderSuffix)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var diagnoses = scope.Analyze();
        var actual = QuerySnapshot.FromScope(scope, diagnoses);

        var snapshotPath = basePath;
        if (applyProviderSuffix && options.ProviderInFileName && SnapshotProvider.SuffixFor(scope) is { } suffix)
        {
            snapshotPath = SnapshotProvider.WithSuffix(basePath, suffix);
            if (!File.Exists(snapshotPath) && File.Exists(basePath) && !options.ShouldUpdate())
            {
                // A snapshot from before provider suffixes: keep honouring it, say so once.
                Emit(scope, options, $"QueryShape: {testName} uses the provider-less snapshot {basePath}; rename it to {Path.GetFileName(snapshotPath)} (or run with {SnapshotOptions.UpdateEnvironmentVariable}=1) to switch to per-provider snapshots");
                snapshotPath = basePath;
            }
        }

        scope.Name ??= testName; // an unnamed scope is this test's scope: reports and telemetry should say which one
        scope.Annotate("snapshot.test", testName);
        scope.Annotate("snapshot.path", snapshotPath);

        if (options.ShouldUpdate())
        {
            await WriteAsync(snapshotPath, actual).ConfigureAwait(false);
            scope.Annotate("snapshot.outcome", "updated");
            Emit(scope, options, $"QueryShape: snapshot updated for {testName} -> {snapshotPath}");
            return new SnapshotResult(SnapshotOutcome.Updated, snapshotPath, actual, diagnoses);
        }

        if (!File.Exists(snapshotPath))
        {
            if (options.IsCi())
            {
                scope.Annotate("snapshot.outcome", "missing");
                throw new QuerySnapshotMissingException(SnapshotMessageBuilder.Missing(testName, snapshotPath), snapshotPath);
            }

            await WriteAsync(snapshotPath, actual).ConfigureAwait(false);
            scope.Annotate("snapshot.outcome", "created");
            Emit(scope, options, $"QueryShape: snapshot created for {testName} ({actual.QueryCount} queries, {actual.Diagnostics.Count} diagnostics) -> {snapshotPath}");
            return new SnapshotResult(SnapshotOutcome.Created, snapshotPath, actual, diagnoses);
        }

        var expected = SnapshotSerializer.Deserialize(await File.ReadAllTextAsync(snapshotPath).ConfigureAwait(false));
        var comparison = SnapshotComparison.Compare(expected, actual, diagnoses, scope.Commands, options.FailOn);
        scope.Annotate("snapshot.expectedQueries", expected.QueryCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        scope.Annotate("snapshot.added", comparison.Added.Sum(a => a.ActualCount - a.ExpectedCount).ToString(System.Globalization.CultureInfo.InvariantCulture));
        scope.Annotate("snapshot.removed", comparison.Removed.Sum(r => r.ExpectedCount - r.ActualCount).ToString(System.Globalization.CultureInfo.InvariantCulture));
        scope.Annotate("snapshot.newDiagnostics", string.Join(",", comparison.NewDiagnoses.Select(d => d.RuleId + " " + d.Severity)));
        if (!comparison.IsMatch)
        {
            scope.Annotate("snapshot.outcome", "mismatch");
            throw new QuerySnapshotMismatchException(SnapshotMessageBuilder.Mismatch(testName, snapshotPath, comparison, options.FailOn), comparison, snapshotPath);
        }

        scope.Annotate("snapshot.outcome", "matched");
        return new SnapshotResult(SnapshotOutcome.Matched, snapshotPath, actual, diagnoses);
    }

    private static async Task WriteAsync(string path, QuerySnapshot snapshot)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, SnapshotSerializer.Serialize(snapshot), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);
    }

    /// <summary>
    /// The "snapshot created/updated" line has to be visible: test runners do not show Trace output, so without <see cref="SnapshotOptions.Log"/>
    /// it goes to the console (shown by dotnet test as the test's standard output), plus Trace and the QueryShape logger when configured.
    /// This package runs only inside test processes, which is why it is allowed to write to the console (CLAUDE.md section 2 applies to the runtime packages).
    /// </summary>
    private static void Emit(QueryShapeScope scope, SnapshotOptions options, string message)
    {
        try
        {
            if (options.Log is { } log)
            {
                log(message);
            }
            else
            {
                Console.Out.WriteLine(message);
                System.Diagnostics.Trace.WriteLine(message);
            }

            scope.Options.LoggerFactory?.CreateLogger("QueryShape").LogInformation("{Message}", message);
        }
        catch
        {
            // Never fail a test because a log sink is broken.
        }
    }
}
