namespace QueryShape.Testing;

/// <summary>One query in a snapshot file.</summary>
/// <param name="Fingerprint">12-hex fingerprint of the shape.</param>
/// <param name="Shape">Normalized SQL.</param>
/// <param name="Source"><c>Linq</c>, <c>Raw</c>, <c>SaveChanges</c> or <c>Other</c>.</param>
/// <param name="Tags">Tags from <c>TagWith</c>.</param>
public sealed record SnapshotQuery(string Fingerprint, string Shape, string Source, IReadOnlyList<string> Tags);

/// <summary>One diagnostic in a snapshot file: known debt that does not fail the test.</summary>
/// <param name="RuleId">Rule id.</param>
/// <param name="Severity">Severity name.</param>
public sealed record SnapshotDiagnostic(string RuleId, string Severity);

/// <summary>The content of a <c>__querysnapshots__/{TestClass}.{TestName}.json</c> file.</summary>
public sealed class QuerySnapshot
{
    /// <summary>Current file format version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>File format version.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Number of queries (equals <c>Queries.Count</c>).</summary>
    public int QueryCount { get; init; }

    /// <summary>Queries, sorted by fingerprint then shape. Duplicates are kept: they are the multiset the test is compared against.</summary>
    public IReadOnlyList<SnapshotQuery> Queries { get; init; } = [];

    /// <summary>Diagnostics present when the snapshot was taken, sorted by rule id then severity.</summary>
    public IReadOnlyList<SnapshotDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Builds a snapshot from a scope: its commands and the diagnoses its rules produce.</summary>
    public static QuerySnapshot FromScope(QueryShapeScope scope, IReadOnlyList<Diagnosis>? diagnoses = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        diagnoses ??= scope.Analyze();

        var queries = scope.Commands
            .Where(c => c.Source != QuerySource.Other)
            .Select(c => new SnapshotQuery(c.Fingerprint, c.Shape, c.Source.ToString(), c.Tags.OrderBy(t => t, StringComparer.Ordinal).ToArray()))
            .OrderBy(q => q.Fingerprint, StringComparer.Ordinal)
            .ThenBy(q => q.Shape, StringComparer.Ordinal)
            .ThenBy(q => q.Source, StringComparer.Ordinal)
            .ToArray();

        var diags = diagnoses
            .Where(d => d.RuleId != QueryShapeScope.OverflowRuleId)
            .Select(d => new SnapshotDiagnostic(d.RuleId, d.Severity.ToString()))
            .OrderBy(d => d.RuleId, StringComparer.Ordinal)
            .ThenBy(d => d.Severity, StringComparer.Ordinal)
            .ToArray();

        return new QuerySnapshot { QueryCount = queries.Length, Queries = queries, Diagnostics = diags };
    }
}
