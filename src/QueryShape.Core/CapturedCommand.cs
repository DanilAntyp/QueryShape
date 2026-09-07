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
    /// <summary>0-based position of the command within its scope (or within the unscoped buffer).</summary>
    public int Sequence { get; internal set; }

    /// <summary>The SQL exactly as sent, including tag comments. Never contains parameter values.</summary>
    public required string CommandText { get; init; }

    /// <summary>Normalized SQL: comments removed, whitespace collapsed, aliases canonicalized. See <see cref="Normalization.SqlNormalizer"/>.</summary>
    public required string Shape { get; init; }

    /// <summary>SHA-256 of <see cref="Shape"/>, first 12 hex characters.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Tags found in leading <c>-- </c> comments (from <c>TagWith</c>).</summary>
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

    /// <summary>Stable 64-bit hash (12 hex characters) over parameter names and values. Lets rules tell "same arguments" from "different arguments" without keeping the values; never persisted.</summary>
    public required string ParameterHash { get; init; }

    /// <summary>Wall-clock time from execute to first result (EF Core's <c>Duration</c>).</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Rows read by the data reader, known once the reader closes; <c>null</c> for non-reader commands or while the reader is open.</summary>
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

    /// <summary>EF Core provider name, e.g. <c>Microsoft.EntityFrameworkCore.Sqlite</c>.</summary>
    public string? ProviderName { get; init; }

    /// <summary>Facts from the LINQ expression, when the command could be correlated with a compilation. Always <c>null</c> for raw commands.</summary>
    public QueryInfo? Query { get; init; }

    /// <summary>First user-code frame, when call-site capture is on.</summary>
    public CallSite? CallSite { get; init; }

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

/// <summary>A <c>SaveChanges</c> call observed in a scope.</summary>
/// <param name="ContextId">Instance id of the DbContext.</param>
/// <param name="ModifiedEntityTypes">CLR names of entity types with Added/Modified/Deleted entries at the time of the call.</param>
/// <param name="EntriesWritten">Number of entries EF Core reported as written.</param>
public sealed record SaveChangesRecord(Guid ContextId, IReadOnlyList<string> ModifiedEntityTypes, int EntriesWritten);
