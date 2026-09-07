using System.Runtime.CompilerServices;

namespace QueryShape.Testing;

/// <summary>Entry point for tests: <c>using var test = QueryShapeTest.Begin(); ... await test.MatchSnapshotAsync();</c>.</summary>
public static class QueryShapeTest
{
    private static QueryShapeOptions? s_defaultOptions;

    /// <summary>Options used for scopes begun by <see cref="Begin"/>: call-site capture is on. Replace or mutate at test-assembly start-up.</summary>
    public static QueryShapeOptions DefaultOptions
    {
        get => s_defaultOptions ??= new QueryShapeOptions { CaptureCallSites = true };
        set => s_defaultOptions = value;
    }

    /// <summary>Begins a scope named after the calling test and remembers where the snapshot for it lives.</summary>
    public static QueryShapeTestScope Begin(
        string? name = null,
        QueryShapeOptions? options = null,
        SnapshotOptions? snapshotOptions = null,
        [CallerFilePath] string callerFilePath = "",
        [CallerMemberName] string callerMemberName = "")
    {
        var testName = name ?? callerMemberName;
        var scope = QueryShapeScope.Begin(testName, options ?? DefaultOptions);
        var path = callerFilePath.Length > 0 ? QueryShapeScopeSnapshotExtensions.ResolvePath(callerFilePath, testName, snapshotOptions) : null;
        var fullName = callerFilePath.Length > 0 ? $"{Path.GetFileNameWithoutExtension(callerFilePath)}.{testName}" : testName;
        return new QueryShapeTestScope(scope, fullName, path, snapshotOptions ?? SnapshotOptions.Default);
    }
}

/// <summary>A scope plus the identity of the test that owns it.</summary>
public sealed class QueryShapeTestScope : IDisposable
{
    internal QueryShapeTestScope(QueryShapeScope scope, string testName, string? snapshotPath, SnapshotOptions snapshotOptions)
    {
        Scope = scope;
        TestName = testName;
        SnapshotPath = snapshotPath;
        SnapshotOptions = snapshotOptions;
    }

    /// <summary>The underlying scope.</summary>
    public QueryShapeScope Scope { get; }

    /// <summary><c>TestClass.TestName</c>.</summary>
    public string TestName { get; }

    /// <summary>Where the snapshot file lives, or <c>null</c> when no caller file path was available.</summary>
    public string? SnapshotPath { get; }

    /// <summary>Snapshot options in effect.</summary>
    public SnapshotOptions SnapshotOptions { get; }

    /// <summary>Compares the scope against the test's snapshot (see <see cref="QueryShapeScopeSnapshotExtensions.MatchSnapshotAsync"/>).</summary>
    public Task<SnapshotResult> MatchSnapshotAsync()
        => Scope.MatchSnapshotFileAsync(SnapshotPath ?? throw new InvalidOperationException("No snapshot path: begin the scope from a test method so [CallerFilePath] is available."), TestName, SnapshotOptions);

    /// <summary>Throws when the scope exceeds <paramref name="budget"/>.</summary>
    public void AssertBudget(QueryBudget budget) => Scope.AssertBudget(budget, TestName);

    /// <summary>Diagnoses for the scope so far.</summary>
    public IReadOnlyList<Diagnosis> Analyze() => Scope.Analyze();

    /// <inheritdoc />
    public void Dispose() => Scope.Dispose();
}
