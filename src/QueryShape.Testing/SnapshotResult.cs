namespace QueryShape.Testing;

/// <summary>What <c>MatchSnapshot</c> did.</summary>
public enum SnapshotOutcome
{
    /// <summary>The run matched the existing snapshot.</summary>
    Matched = 0,

    /// <summary>No snapshot existed; one was written (local mode only).</summary>
    Created = 1,

    /// <summary>The snapshot was rewritten because updates were requested.</summary>
    Updated = 2,
}

/// <summary>Result of a successful snapshot match.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Path">Snapshot file path.</param>
/// <param name="Snapshot">The snapshot of this run.</param>
/// <param name="Diagnoses">All diagnoses of this run, including known ones.</param>
public sealed record SnapshotResult(SnapshotOutcome Outcome, string Path, QuerySnapshot Snapshot, IReadOnlyList<Diagnosis> Diagnoses);
