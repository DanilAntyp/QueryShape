using System.Data;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace QueryShape;

/// <summary>A parameter of a captured command. Values are only present when <see cref="QueryShapeOptions.IncludeParameterValues"/> is on.</summary>
/// <param name="Name">Parameter name as sent to the provider.</param>
/// <param name="DbType">Provider-neutral type.</param>
/// <param name="Direction">Input/Output.</param>
/// <param name="Value">The value, or <c>null</c> unless values were opted in. <see cref="DBNull"/> is reported as <c>null</c> with <paramref name="IsNull"/> set.</param>
/// <param name="IsNull"><c>true</c> when the value sent was <c>NULL</c>.</param>
public sealed record CapturedParameter(string Name, DbType DbType, ParameterDirection Direction, object? Value, bool IsNull);

/// <summary>One database command as executed by EF Core (or a wrapped raw connection).</summary>
public sealed class CapturedCommand
{
    /// <summary>0-based capture order within the outermost enclosing scope (or within the unscoped buffer); orders commands consistently in every nested scope.</summary>
    public int Sequence { get; internal set; }

    /// <summary>
    /// The SQL exactly as sent, including tag comments. EF Core sends values as parameters, so for a LINQ query this holds only constants from the source;
    /// raw SQL (<see cref="QuerySource.Raw"/>) may embed values. Never exported: reports, snapshots and telemetry use <see cref="Shape"/>.
    /// </summary>
    public required string CommandText { get; init; }

    /// <summary>Normalized SQL: comments removed, whitespace collapsed, aliases and parameter names canonicalized; for raw SQL, literals masked as <c>?</c>. See <see cref="Normalization.SqlNormalizer"/>.</summary>
    public required string Shape { get; init; }

    /// <summary>SHA-256 of <see cref="Shape"/>, first 12 hex characters.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Tags from <c>TagWith</c>. EF Core's <c>TagWithCallSite()</c> tag is parsed into <see cref="CallSite"/> instead of being listed here (it holds a machine-specific path).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Where the command came from.</summary>
    public required QuerySource Source { get; init; }

    /// <summary>EF Core's own classification of the command.</summary>
    public CommandSource CommandSource { get; init; }

    /// <summary>Text / StoredProcedure / TableDirect.</summary>
    public CommandType CommandType { get; init; } = CommandType.Text;

    /// <summary>Which execute method was used.</summary>
    public DbCommandMethod ExecuteMethod { get; init; }

    /// <summary>Parameters as sent. Values are stripped unless opted in.</summary>
    public IReadOnlyList<CapturedParameter> Parameters { get; init; } = [];

    /// <summary>Stable 64-bit hash (12 hex characters) over parameter names and values (and, for raw SQL, the text). Lets rules tell "same arguments" from "different arguments" without keeping the values; never persisted.</summary>
    public required string ParameterHash { get; init; }

    /// <summary>Wall-clock time from execute to first result (EF Core's <c>Duration</c>).</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Rows read by a query data reader, known once it closes; <c>null</c> for SaveChanges, non-reader commands, or while the reader is open.</summary>
    public int? RowsReturned { get; internal set; }

    /// <summary>Rows affected as reported by the provider, when known.</summary>
    public int? RowsAffected { get; internal set; }

    /// <summary>
    /// For queries with two or more collection includes: the number of distinct values seen in the first result column
    /// (EF Core orders such results by the root key, which is projected first), i.e. an estimate of distinct root entities. Otherwise <c>null</c>.
    /// </summary>
    public int? DistinctRootsEstimate { get; internal set; }

    /// <summary>The largest number of elements passed in a collection parameter (JSON array, provider array) or an inline <c>IN (...)</c> list, when any.</summary>
    public int? MaxCollectionParameterCount { get; init; }

    /// <summary>Entities that started being tracked by the change tracker as results of this command (observed through <c>ChangeTracker.Tracked</c>).</summary>
    public int TrackedEntities { get; internal set; }

    /// <summary>
    /// Whether this command's results were tracked: <c>true</c> when tracking was observed or the query was compiled by this context with tracking on,
    /// <c>false</c> when compiled by this context without tracking (or returning no entities), <c>null</c> when it cannot be known
    /// (raw SQL, or expression info inferred from a query compiled elsewhere; see ADR-0002).
    /// </summary>
    public bool? IsTracking { get; internal set; }

    /// <summary>EF Core provider name, e.g. <c>Microsoft.EntityFrameworkCore.Sqlite</c>.</summary>
    public string? ProviderName { get; init; }

    /// <summary>Facts from the LINQ expression, when the command could be correlated with a compilation. Always <c>null</c> for raw commands.</summary>
    public QueryInfo? Query { get; init; }

    /// <summary>First user-code frame, when known (see <see cref="CallSiteOrigin"/>).</summary>
    public CallSite? CallSite { get; init; }

    /// <summary>How <see cref="CallSite"/> was obtained.</summary>
    public CallSiteOrigin CallSiteOrigin { get; init; }

    /// <summary>
    /// User-code frames the query was reached through, innermost first, when the stack was walked with <see cref="QueryShapeOptions.CallPathDepth"/> &gt; 1.
    /// The first entry is the frame that issued the query; <see cref="CallSite"/> is the one it is attributed to, which differs when infrastructure frames were skipped.
    /// </summary>
    public IReadOnlyList<CallSite> CallPath { get; init; } = [];

    /// <summary>EF Core's command id.</summary>
    public Guid CommandId { get; init; }

    /// <summary>EF Core's connection id.</summary>
    public Guid ConnectionId { get; init; }

    /// <summary>Instance id of the DbContext that ran the command, or <c>null</c> for raw commands.</summary>
    public Guid? ContextId { get; init; }

    /// <summary>When execution started.</summary>
    public DateTimeOffset StartTime { get; init; }

    /// <summary><c>true</c> for the async execute methods.</summary>
    public bool IsAsync { get; init; }

    /// <summary><c>true</c> when the provider threw.</summary>
    public bool Failed { get; init; }

    /// <summary>Exception type name when <see cref="Failed"/>.</summary>
    public string? ErrorType { get; init; }

    /// <summary>Best available description of where this query came from: call site, first tag, or expression root.</summary>
    public string Origin
        => CallSite?.ToString()
           ?? (Tags.Count > 0 ? Tags[0] : null)
           ?? Query?.RootEntityShortName
           ?? Source.ToString();

    /// <inheritdoc />
    public override string ToString() => $"#{Sequence} {Source} {Fingerprint} {Duration.TotalMilliseconds:0.0}ms {Shape}";
}

/// <summary>Where a command's call site came from.</summary>
public enum CallSiteOrigin
{
    /// <summary>No call site.</summary>
    None = 0,

    /// <summary>EF Core's <c>TagWithCallSite()</c> tag in the SQL (zero cost).</summary>
    Tag = 1,

    /// <summary>Stack walk because <see cref="QueryShapeOptions.CaptureCallSites"/> is on.</summary>
    StackWalk = 2,

    /// <summary>Stack walk because this execution was the sampled one (<see cref="QueryShapeOptions.CallSiteSamplingInterval"/>).</summary>
    Sampled = 3,

    /// <summary>Reused from an earlier sampled execution of the same query shape; not exported to telemetry.</summary>
    Cached = 4,
}

/// <summary>A <c>SaveChanges</c> call observed in a scope.</summary>
/// <param name="ContextId">Instance id of the DbContext.</param>
/// <param name="ModifiedEntityTypes">CLR names of entity types that were added, modified or deleted since the previous SaveChanges on the context (observed through change-tracker events).</param>
/// <param name="EntriesWritten">Number of entries EF Core reported as written (0 when the save failed).</param>
public sealed record SaveChangesRecord(Guid ContextId, IReadOnlyList<string> ModifiedEntityTypes, int EntriesWritten);
