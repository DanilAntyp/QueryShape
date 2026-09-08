using System.Collections.Concurrent;
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

    private static readonly AsyncLocal<QueryInfo?> s_compiling = new();
    private volatile QueryInfo? _lastCompiledAnywhere;

    private readonly ConditionalWeakTable<DbContext, ContextState> _states = new();
    private readonly ConcurrentDictionary<string, QueryInfo> _byFingerprint = new(StringComparer.Ordinal);
    private readonly int _maxCacheEntries;

    public ExpressionCorrelator(int maxCacheEntries = 5000)
    {
        _maxCacheEntries = maxCacheEntries;
    }

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

    public (QueryInfo? Info, Resolution Resolution) Resolve(DbContext? context, string fingerprint, string shape)
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
                    if (Matches(pending, shape))
                    {
                        state.Pending = null;
                        Remember(fingerprint, pending);
                        return (pending, Resolution.OwnCompilation);
                    }

                    // A compilation that never executed (translation failure) - forget it.
                    state.Pending = null;
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
                if (state.Last is { } last && Matches(last, shape))
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

    private static bool Matches(QueryInfo info, string shape)
        => info.RootTableName is null || shape.Contains(info.RootTableName, StringComparison.Ordinal);

    private void Remember(string fingerprint, QueryInfo info)
    {
        if (_byFingerprint.Count >= _maxCacheEntries)
        {
            _byFingerprint.Clear();
        }

        _byFingerprint[fingerprint] = info;
    }
}
