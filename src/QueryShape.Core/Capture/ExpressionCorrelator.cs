using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace QueryShape.Capture;

/// <summary>
/// Links compiled LINQ expressions to the SQL commands they produce. EF Core fires <c>QueryCompilationStarting</c>
/// only on a compiled-query-cache miss and gives us no id shared with the later command, so we match on order
/// within the same DbContext and remember fingerprint → expression for later cache hits. See ADR-0002.
/// </summary>
internal sealed class ExpressionCorrelator
{
    private sealed class ContextState
    {
        public QueryInfo? Pending;
        public QueryInfo? Last;
    }

    /// <summary>How a command's expression info was found.</summary>
    public enum Resolution
    {
        /// <summary>No expression info.</summary>
        None,

        /// <summary>This context compiled the query: the info describes exactly this LINQ query.</summary>
        OwnCompilation,

        /// <summary>Taken from the fingerprint cache: the same SQL, possibly compiled from a differently-tracked variant elsewhere.</summary>
        Cache,
    }

    /// <summary>SQL that a query with a row-limiting or aggregating operator must contain a trace of (any provider).</summary>
    private static readonly string[] s_limitMarkers =
    [
        "TOP", "LIMIT", "FETCH", "OFFSET", "EXISTS", "COUNT(", "MIN(", "MAX(", "SUM(", "AVG(", "ROWNUM", "FIRST", "ROWS",
    ];

    private static readonly AsyncLocal<QueryInfo?> s_compiling = new();
    private static int s_plannedObserved;
    private volatile QueryInfo? _lastCompiledAnywhere;

    private readonly ConditionalWeakTable<DbContext, ContextState> _states = new();
    private readonly ConcurrentDictionary<string, QueryInfo> _byFingerprint = new(StringComparer.Ordinal);
    private readonly int _maxCacheEntries;
    private int _cacheEntries;

    public ExpressionCorrelator(int maxCacheEntries = 5000)
    {
        _maxCacheEntries = maxCacheEntries;
    }

    /// <summary>For tests and debugging: why the last <see cref="Matches"/> call said no (<c>null</c> when it matched).</summary>
    internal static string? LastMismatchReason { get; private set; }

    /// <summary><c>true</c> once EF Core has been seen finishing a compilation (its <c>QueryExecutionPlanned</c> event); from then on a compilation without it is one that failed.</summary>
    internal static bool PlannedEventsObserved => Volatile.Read(ref s_plannedObserved) == 1;

    public void OnCompiled(DbContext? context, QueryInfo info)
    {
        if (context is null)
        {
            return;
        }

        var state = _states.GetOrCreateValue(context);
        lock (state)
        {
            state.Pending = info;
            state.Last = info;
        }

        // Compilation continues synchronously on this async flow; warnings raised during it find the query here first.
        s_compiling.Value = info;
        _lastCompiledAnywhere = info;
    }

    /// <summary>EF Core finished compiling (translated and planned) the query being compiled on <paramref name="context"/>: it may now execute.</summary>
    public void OnPlanned(DbContext? context)
    {
        Volatile.Write(ref s_plannedObserved, 1);
        var target = s_compiling.Value;
        if (target is null && context is not null && _states.TryGetValue(context, out var state))
        {
            lock (state)
            {
                target = state.Pending ?? state.Last;
            }
        }

        if (target is not null)
        {
            target.Planned = true;
        }
    }

    /// <summary>Attaches an EF Core compile-time warning to the query being compiled on <paramref name="context"/> (or, without a context, to the latest compilation anywhere).</summary>
    public void OnWarning(DbContext? context, string warning)
    {
        var target = s_compiling.Value;
        if (target is null && context is not null && _states.TryGetValue(context, out var state))
        {
            lock (state)
            {
                target = state.Pending ?? state.Last;
            }
        }

        (target ?? _lastCompiledAnywhere)?.AddWarning(warning);
    }

    public (QueryInfo? Info, Resolution Resolution) Resolve(DbContext? context, string fingerprint, string shape, DbParameterCollection? parameters)
    {
        ContextState? state = null;
        if (context is not null)
        {
            _states.TryGetValue(context, out state);
        }

        if (state is not null)
        {
            lock (state)
            {
                if (state.Pending is { } pending)
                {
                    // Whatever happens, the pending compilation is used at most once: the command that follows it is either its execution or proof it never ran.
                    state.Pending = null;
                    if (Matches(pending, shape, parameters))
                    {
                        Remember(fingerprint, pending);
                        return (pending, Resolution.OwnCompilation);
                    }
                }
            }
        }

        if (_byFingerprint.TryGetValue(fingerprint, out var known))
        {
            return (known, Resolution.Cache);
        }

        if (state is not null)
        {
            lock (state)
            {
                // Split queries: several commands for one compilation.
                if (state.Last is { } last && Matches(last, shape, parameters))
                {
                    Remember(fingerprint, last);
                    return (last, Resolution.OwnCompilation);
                }
            }
        }

        return (null, Resolution.None);
    }

    /// <summary>For tests: whether a fingerprint has been associated.</summary>
    internal bool TryGetKnown(string fingerprint, out QueryInfo? info) => _byFingerprint.TryGetValue(fingerprint, out info);

    /// <summary>
    /// Whether a compiled expression can be the origin of this SQL. Every check is a necessary condition, so a mismatch proves the compilation
    /// was something else (a <c>ToQueryString()</c>, a translation that threw, an enumeration that never started); a match is still a heuristic.
    /// </summary>
    internal static bool Matches(QueryInfo info, string shape, DbParameterCollection? parameters)
    {
        LastMismatchReason = Mismatch(info, shape, parameters);
        return LastMismatchReason is null;
    }

    private static string? Mismatch(QueryInfo info, string shape, DbParameterCollection? parameters)
    {
        if (PlannedEventsObserved && !info.Planned)
        {
            return "not planned"; // EF Core never finished compiling it, so it never ran
        }

        if (info.RootTableName is not null && !shape.Contains(info.RootTableName, StringComparison.Ordinal))
        {
            return "table " + info.RootTableName + " absent";
        }

        if (info.HasFilter && !shape.Contains("WHERE", StringComparison.OrdinalIgnoreCase))
        {
            return "filter without WHERE";
        }

        if (info.KeyFilters.Count > 0 && !shape.Contains("@p", StringComparison.Ordinal))
        {
            return "key filter without parameter"; // a key compared to a query parameter always leaves a parameter in the SQL
        }

        if (info.CollectionIncludes.Count > 0 && !info.HasProjection && info.SplittingBehavior == "SingleQuery" && !shape.Contains("JOIN", StringComparison.OrdinalIgnoreCase))
        {
            return "collection include without JOIN";
        }

        if (info.HasLimit && !s_limitMarkers.Any(m => shape.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return "limit without row limiting SQL";
        }

        if (info.ParameterNames.Count > 0 && parameters is { Count: > 0 } && !SharesAParameterName(info.ParameterNames, parameters))
        {
            return "parameters " + string.Join(",", info.ParameterNames) + " not among " + string.Join(",", parameters.Cast<DbParameter>().Select(p => p.ParameterName));
        }

        return null;
    }

    /// <summary>
    /// EF Core names SQL parameters after the expression's query parameters (<c>@__id_0</c> in EF Core 8, <c>@id</c> in EF Core 10) or derives them from one
    /// (a collection expanded to <c>@ids1</c>, <c>@ids2</c>...), so a command from this compilation has at least one name equal to or starting with an expected one.
    /// </summary>
    private static bool SharesAParameterName(IReadOnlyList<string> expected, DbParameterCollection parameters)
    {
        foreach (DbParameter p in parameters)
        {
            var name = p.ParameterName.TrimStart('@', ':', '$', '?');
            foreach (var e in expected)
            {
                if (name.StartsWith(e, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void Remember(string fingerprint, QueryInfo info)
    {
        if (_byFingerprint.TryAdd(fingerprint, info))
        {
            if (Interlocked.Increment(ref _cacheEntries) > _maxCacheEntries)
            {
                _byFingerprint.Clear();
                Interlocked.Exchange(ref _cacheEntries, 0);
            }

            return;
        }

        _byFingerprint[fingerprint] = info;
    }
}
