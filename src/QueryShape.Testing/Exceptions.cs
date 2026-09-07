namespace QueryShape.Testing;

/// <summary>Thrown when a run does not match its snapshot. The message is the full, readable diff.</summary>
public sealed class QuerySnapshotMismatchException : Exception
{
    /// <summary>Creates the exception.</summary>
    public QuerySnapshotMismatchException(string message, SnapshotComparison comparison, string snapshotPath)
        : base(message)
    {
        Comparison = comparison;
        SnapshotPath = snapshotPath;
    }

    /// <summary>The comparison that failed.</summary>
    public SnapshotComparison Comparison { get; }

    /// <summary>Path of the snapshot file.</summary>
    public string SnapshotPath { get; }
}

/// <summary>Thrown in CI mode when a snapshot file does not exist.</summary>
public sealed class QuerySnapshotMissingException : Exception
{
    /// <summary>Creates the exception.</summary>
    public QuerySnapshotMissingException(string message, string snapshotPath)
        : base(message)
    {
        SnapshotPath = snapshotPath;
    }

    /// <summary>Path of the missing snapshot file.</summary>
    public string SnapshotPath { get; }
}

/// <summary>Thrown when a scope exceeds a <see cref="QueryBudget"/>.</summary>
public sealed class QueryBudgetExceededException : Exception
{
    /// <summary>Creates the exception.</summary>
    public QueryBudgetExceededException(string message, IReadOnlyList<string> violations)
        : base(message)
    {
        Violations = violations;
    }

    /// <summary>One line per violated limit.</summary>
    public IReadOnlyList<string> Violations { get; }
}
