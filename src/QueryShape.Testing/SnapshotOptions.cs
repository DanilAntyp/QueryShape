namespace QueryShape.Testing;

/// <summary>Controls how snapshots are located, compared and updated.</summary>
public sealed class SnapshotOptions
{
    /// <summary>Environment variable that forces snapshots to be rewritten when set to <c>1</c> or <c>true</c>.</summary>
    public const string UpdateEnvironmentVariable = "QUERYSHAPE_UPDATE_SNAPSHOTS";

    /// <summary>Environment variable whose presence (<c>CI=true</c>) turns a missing snapshot into a failure.</summary>
    public const string CiEnvironmentVariable = "CI";

    /// <summary>Process-wide defaults used when a call passes no options.</summary>
    public static SnapshotOptions Default { get; } = new();

    /// <summary>A new diagnosis fails the test when its severity is at or above this. Default <see cref="Severity.Warning"/>.</summary>
    public Severity FailOn { get; set; } = Severity.Warning;

    /// <summary>Directory (relative to the test source file) that holds snapshot files. Default <c>__querysnapshots__</c>.</summary>
    public string DirectoryName { get; set; } = "__querysnapshots__";

    /// <summary>Force update (<c>true</c>) or forbid it (<c>false</c>). <c>null</c> reads <c>QUERYSHAPE_UPDATE_SNAPSHOTS</c>.</summary>
    public bool? UpdateSnapshots { get; set; }

    /// <summary>Force CI mode (<c>true</c>: missing snapshot fails) or local mode (<c>false</c>). <c>null</c> reads the <c>CI</c> variable.</summary>
    public bool? CiMode { get; set; }

    /// <summary>Environment lookup; replaceable for tests. Defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
    public Func<string, string?> Environment { get; set; } = System.Environment.GetEnvironmentVariable;

    /// <summary>Receives one line when a snapshot is created or updated. Defaults to <see cref="System.Diagnostics.Trace"/> plus the QueryShape logger when configured.</summary>
    public Action<string>? Log { get; set; }

    internal bool ShouldUpdate()
    {
        if (UpdateSnapshots is { } forced)
        {
            return forced;
        }

        var value = Environment(UpdateEnvironmentVariable);
        return value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    internal bool IsCi()
    {
        if (CiMode is { } forced)
        {
            return forced;
        }

        var value = Environment(CiEnvironmentVariable);
        return value is not null && value.Length > 0 && !value.Equals("false", StringComparison.OrdinalIgnoreCase) && value != "0";
    }
}
