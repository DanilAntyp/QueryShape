using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using QueryShape.Internal;
using QueryShape.Normalization;

namespace QueryShape.Capture;

/// <summary>Shared capture pipeline for EF Core commands and wrapped raw commands. Never throws.</summary>
internal sealed class CommandCapturer
{
    private const int NormalizationCacheLimit = 2048;

    private readonly object _gate = new();
    private readonly Queue<CapturedCommand> _unscoped = new();
    private readonly ConditionalWeakTable<DbContext, ContextState> _contexts = new();
    private readonly ConcurrentDictionary<string, NormalizedSql> _normalized = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NormalizedSql> _normalizedRaw = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, CapturedCommand> _openReaders = new();
    private readonly ConcurrentDictionary<Guid, CommandStart> _pendingStarts = new();
    private int _pendingStartCount;
    private readonly ConcurrentDictionary<string, int> _sampleCounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CallSite> _sampledCallSites = new(StringComparer.Ordinal);
    private int _unscopedSequence;

    public ExpressionCorrelator Correlator { get; } = new();

    /// <summary>Per-DbContext-instance state: options, provider name, and the reader currently being consumed (for tracking observation).</summary>
    private sealed class ContextState
    {
        private readonly SortedSet<string> _dirtyTypes = new(StringComparer.Ordinal);

        public required QueryShapeOptions Options { get; init; }

        public string? ProviderName { get; set; }

        public CapturedCommand? ActiveReader { get; set; }

        /// <summary>An entity of this type became Added/Modified/Deleted: it belongs to the write set of the next SaveChanges.</summary>
        public void MarkDirty(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
        {
            var name = entry.Metadata.ClrType.FullName ?? entry.Metadata.Name;
            lock (_dirtyTypes)
            {
                _dirtyTypes.Add(name);
            }
        }

        public string[] TakeDirtyTypes()
        {
            lock (_dirtyTypes)
            {
                var result = _dirtyTypes.ToArray();
                _dirtyTypes.Clear();
                return result;
            }
        }
    }

    /// <summary>Options for a context: from its <see cref="QueryShapeOptionsExtension"/>, else <see cref="QueryShapeOptions.Default"/>.</summary>
    public QueryShapeOptions OptionsFor(DbContext? context) => context is null ? QueryShapeOptions.Default : StateFor(context).Options;

    private ContextState StateFor(DbContext context)
    {
        if (_contexts.TryGetValue(context, out var existing))
        {
            return existing;
        }

        QueryShapeOptions resolved;
        try
        {
            resolved = context.GetService<IDbContextOptions>().FindExtension<QueryShapeOptionsExtension>()?.Options ?? QueryShapeOptions.Default;
        }
        catch
        {
            resolved = QueryShapeOptions.Default;
        }

        var state = new ContextState { Options = resolved };
        if (!_contexts.TryAdd(context, state))
        {
            return _contexts.TryGetValue(context, out var raced) ? raced : state;
        }

        if (resolved.Enabled)
        {
            try
            {
                // Entities that start being tracked while a reader is open are this command's results: tracking observed, not inferred (ADR-0002).
                // Entities attached or transitioning as Added/Modified/Deleted are the write set of the next SaveChanges: observed through the
                // change tracker's own events, so QueryShape never runs DetectChanges itself.
                context.ChangeTracker.Tracked += (_, e) =>
                {
                    if (e.FromQuery)
                    {
                        if (state.ActiveReader is { } active)
                        {
                            active.TrackedEntities++;
                        }
                    }
                    else if (e.Entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                    {
                        state.MarkDirty(e.Entry);
                    }
                };
                context.ChangeTracker.StateChanged += (_, e) =>
                {
                    if (e.NewState is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                    {
                        state.MarkDirty(e.Entry);
                    }
                };
            }
            catch (Exception ex)
            {
                Log.Swallowed(resolved, "change tracker subscription", ex);
            }
        }

        return state;
    }

    public IReadOnlyList<CapturedCommand> RecentUnscopedCommands
    {
        get { lock (_gate) { return _unscoped.ToArray(); } }
    }

    /// <summary>
    /// Right before a command executes: when listeners are registered, resolve what is already known (shape, tags, call site) and tell them,
    /// so they can mark the provider's span while it is current. The work is reused by <see cref="Capture"/>; nothing runs without listeners.
    /// </summary>
    public void Starting(QueryShapeOptions options, DbCommand command, QuerySource source, Guid commandId, DateTimeOffset startTime)
    {
        if (!options.Enabled)
        {
            return;
        }

        try
        {
            var scope = QueryShapeScope.Current;
            if (options.Listeners.Count == 0 && (scope?.Options.Listeners.Count ?? 0) == 0)
            {
                return;
            }

            var normalized = Normalize(command.CommandText ?? string.Empty, maskLiterals: source == QuerySource.Raw);
            var (callSite, origin) = ResolveCallSite(options, scope, normalized.Tags, normalized.Fingerprint);
            var start = new CommandStart(commandId, normalized.Fingerprint, normalized.Shape, source, normalized.Tags, callSite, origin, startTime);

            if (Interlocked.Increment(ref _pendingStartCount) > 10_000)
            {
                _pendingStarts.Clear(); // commands that never reported back; keep memory bounded
                Interlocked.Exchange(ref _pendingStartCount, 0);
            }

            _pendingStarts[commandId] = start;

            foreach (var listener in ListenersFor(options, scope))
            {
                try
                {
                    listener.OnCommandExecuting(start, scope);
                }
                catch (Exception ex)
                {
                    Log.Swallowed(options, "listener.OnCommandExecuting", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Swallowed(options, "command starting", ex);
        }
    }

    private static IEnumerable<IQueryShapeListener> ListenersFor(QueryShapeOptions options, QueryShapeScope? scope)
        => scope?.Options.Listeners.Count > 0 && !ReferenceEquals(scope.Options, options)
            ? options.Listeners.Concat(scope.Options.Listeners).Distinct()
            : options.Listeners;

    /// <summary>Captures one command. Returns the captured command, or <c>null</c> when capture failed internally.</summary>
    public CapturedCommand? Capture(
        QueryShapeOptions options,
        DbCommand command,
        QuerySource source,
        CommandSource commandSource,
        DbCommandMethod executeMethod,
        Guid commandId,
        Guid connectionId,
        DbContext? context,
        DateTimeOffset startTime,
        TimeSpan duration,
        bool isAsync,
        object? result,
        Exception? error)
    {
        if (!options.Enabled)
        {
            return null;
        }

        try
        {
            // Raw SQL may embed values in its text: its shape masks literals, and its parameter hash covers the text so variants still differ.
            var raw = source == QuerySource.Raw;
            var normalized = Normalize(command.CommandText ?? string.Empty, maskLiterals: raw);
            var scope = QueryShapeScope.Current;

            QueryInfo? query = null;
            var resolution = ExpressionCorrelator.Resolution.None;
            if (commandSource is CommandSource.LinqQuery or CommandSource.FromSqlQuery or CommandSource.ExecuteUpdate or CommandSource.ExecuteDelete)
            {
                (query, resolution) = Correlator.Resolve(context, normalized.Fingerprint, normalized.Shape, command.Parameters);
            }

            var contextState = context is null ? null : StateFor(context);

            // Tags: the expression tree has them exactly; the SQL comment block is the fallback (EF Core joins tags into one block).
            var tags = query is { Tags.Count: > 0 } ? query.Tags : normalized.Tags;

            // The Executing side may have resolved the call site already (and consumed the sampling slot): reuse it rather than walking twice.
            CallSite? callSite;
            CallSiteOrigin callSiteOrigin;
            if (_pendingStarts.TryRemove(commandId, out var start))
            {
                Interlocked.Decrement(ref _pendingStartCount);
                (callSite, callSiteOrigin) = (start.CallSite, start.CallSiteOrigin);
            }
            else
            {
                (callSite, callSiteOrigin) = ResolveCallSite(options, scope, tags, normalized.Fingerprint);
            }

            var parameters = CaptureParameters(command, options.IncludeParameterValues, raw ? command.CommandText : null, out var parameterHash, out var maxCollectionCount);
            maxCollectionCount ??= InlineListCount(normalized.Shape);

            var captured = new CapturedCommand
            {
                CommandText = command.CommandText ?? string.Empty,
                Shape = normalized.Shape,
                Fingerprint = normalized.Fingerprint,
                Tags = tags,
                Source = source,
                CommandSource = commandSource,
                CommandType = command.CommandType,
                ExecuteMethod = executeMethod,
                Parameters = parameters,
                ParameterHash = parameterHash,
                MaxCollectionParameterCount = maxCollectionCount,
                Duration = duration,
                ProviderName = contextState is null ? null : (contextState.ProviderName ??= SafeProviderName(context)),
                Query = query,
                CallSite = callSite,
                CallSiteOrigin = callSiteOrigin,
                CommandId = commandId,
                ConnectionId = connectionId,
                ContextId = context?.ContextId.InstanceId,
                StartTime = startTime,
                IsAsync = isAsync,
                Failed = error is not null,
                ErrorType = error?.GetType().Name,
            };

            if (executeMethod == DbCommandMethod.ExecuteNonQuery && result is int affected)
            {
                captured.RowsAffected = affected;
            }

            // Tracking: certain only from this context's own compilation; a cache hit may describe a differently-tracked variant with the same SQL.
            captured.IsTracking = resolution == ExpressionCorrelator.Resolution.OwnCompilation ? query!.ReturnsEntities && query.IsTracking : null;

            if (executeMethod == DbCommandMethod.ExecuteReader && error is null)
            {
                if (contextState is not null)
                {
                    contextState.ActiveReader = captured;
                }

                if (_openReaders.Count > 10_000)
                {
                    _openReaders.Clear(); // readers that were never closed; keep memory bounded
                }

                _openReaders[commandId] = captured;
            }

            foreach (var listener in ListenersFor(options, scope))
            {
                try
                {
                    listener.OnCommandCaptured(captured, scope);
                }
                catch (Exception ex)
                {
                    Log.Swallowed(options, "listener.OnCommandCaptured", ex);
                }
            }

            if (scope is null)
            {
                lock (_gate)
                {
                    captured.Sequence = _unscopedSequence++;
                    _unscoped.Enqueue(captured);
                    while (_unscoped.Count > Math.Max(0, options.UnscopedBufferSize))
                    {
                        _unscoped.Dequeue();
                    }
                }
            }
            else
            {
                scope.Record(captured);
            }

            return captured;
        }
        catch (Exception ex)
        {
            Log.Swallowed(options, "capture", ex);
            return null;
        }
    }

    /// <summary>A SaveChanges finished (or failed): records the entity types it was writing, observed through change-tracker events since the previous one.</summary>
    public void SaveChangesCompleted(DbContext context, int entriesWritten)
    {
        try
        {
            var state = StateFor(context);
            var types = state.TakeDirtyTypes(); // always taken, so the next save starts clean even outside a scope
            var scope = QueryShapeScope.Current;
            if (scope is null || !state.Options.Enabled)
            {
                return;
            }

            scope.Record(new SaveChangesRecord(context.ContextId.InstanceId, types, entriesWritten));
        }
        catch (Exception ex)
        {
            Log.Swallowed(OptionsFor(context), "save changes", ex);
        }
    }

    public void ReaderClosed(Guid commandId, int readCount, int recordsAffected, int? distinctRoots = null)
    {
        try
        {
            if (!_openReaders.TryRemove(commandId, out var command))
            {
                return;
            }

            command.RowsReturned = readCount;
            command.DistinctRootsEstimate = distinctRoots;
            if (recordsAffected >= 0 && command.RowsAffected is null && command.ExecuteMethod != DbCommandMethod.ExecuteReader)
            {
                command.RowsAffected = recordsAffected;
            }

            if (command.TrackedEntities > 0)
            {
                command.IsTracking = true; // observed, whatever the expression info said
            }

            QueryShapeScope.Current?.InvalidateAnalysis();
        }
        catch (Exception ex)
        {
            Log.Swallowed(null, "reader closed", ex);
        }
    }

    /// <summary>Tag first (free), then a full stack walk when asked for, then sampling: first execution of a shape and every N-th after it.</summary>
    private (CallSite? Site, CallSiteOrigin Origin) ResolveCallSite(QueryShapeOptions options, QueryShapeScope? scope, IReadOnlyList<string> tags, string fingerprint)
    {
        if (CallSiteCapture.FromTags(tags) is { } tagged)
        {
            return (tagged, CallSiteOrigin.Tag);
        }

        if (options.CaptureCallSites || (scope?.Options.CaptureCallSites ?? false))
        {
            var walked = CallSiteCapture.Capture();
            return (walked, walked is null ? CallSiteOrigin.None : CallSiteOrigin.StackWalk);
        }

        var interval = options.CallSiteSamplingInterval;
        if (interval <= 0)
        {
            return (null, CallSiteOrigin.None);
        }

        if (_sampleCounts.Count >= NormalizationCacheLimit)
        {
            _sampleCounts.Clear();
        }

        var count = _sampleCounts.AddOrUpdate(fingerprint, 0, static (_, c) => c + 1);
        if (count % interval == 0)
        {
            var sampled = CallSiteCapture.Capture();
            if (sampled is not null)
            {
                if (_sampledCallSites.Count >= NormalizationCacheLimit)
                {
                    _sampledCallSites.Clear();
                }

                _sampledCallSites[fingerprint] = sampled;
                return (sampled, CallSiteOrigin.Sampled);
            }

            return (null, CallSiteOrigin.None);
        }

        return _sampledCallSites.TryGetValue(fingerprint, out var cached) ? (cached, CallSiteOrigin.Cached) : (null, CallSiteOrigin.None);
    }

    /// <summary>The same SQL text is executed over and over (compiled query cache); normalize each distinct text once.</summary>
    private NormalizedSql Normalize(string commandText, bool maskLiterals)
    {
        var cache = maskLiterals ? _normalizedRaw : _normalized;
        if (cache.TryGetValue(commandText, out var cached))
        {
            return cached;
        }

        var normalized = SqlNormalizer.Normalize(commandText, maskLiterals);
        if (cache.Count >= NormalizationCacheLimit)
        {
            cache.Clear();
        }

        cache[commandText] = normalized;
        return normalized;
    }

    private static string? SafeProviderName(DbContext? context)
    {
        try
        {
            return context?.Database.ProviderName;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<CapturedParameter> CaptureParameters(DbCommand command, bool includeValues, string? textToHash, out string parameterHash, out int? maxCollectionCount)
    {
        maxCollectionCount = null;
        var count = command.Parameters.Count;
        if (count == 0 && textToHash is null)
        {
            parameterHash = "000000000000";
            return [];
        }

        var list = new CapturedParameter[count];
        var hash = textToHash is null ? Fnv.Offset : Fnv.Add(Fnv.Offset, textToHash);
        for (var i = 0; i < count; i++)
        {
            var p = command.Parameters[i];
            var value = p.Value;
            var isNull = value is null || value is DBNull;
            hash = Fnv.Add(hash, p.ParameterName);
            hash = isNull ? Fnv.Add(hash, "NULL") : Fnv.AddValue(hash, value!);
            list[i] = new CapturedParameter(p.ParameterName, p.DbType, p.Direction, includeValues && !isNull ? value : null, isNull);

            var elements = CollectionElementCount(value);
            if (elements is { } n && (maxCollectionCount is null || n > maxCollectionCount))
            {
                maxCollectionCount = n;
            }
        }

        parameterHash = hash.ToString("x12", CultureInfo.InvariantCulture)[..12];
        return list;
    }

    /// <summary>64-bit FNV-1a over UTF-16 code units: deterministic, allocation-free, fast. Only ever compared within a process.</summary>
    private static class Fnv
    {
        public const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        public static ulong Add(ulong hash, string text)
        {
            foreach (var ch in text)
            {
                hash = (hash ^ ch) * Prime;
            }

            return (hash ^ 0x1F) * Prime; // separator
        }

        public static ulong Add(ulong hash, ulong bits)
        {
            for (var i = 0; i < 8; i++)
            {
                hash = (hash ^ (bits & 0xFF)) * Prime;
                bits >>= 8;
            }

            return (hash ^ 0x1F) * Prime;
        }

        /// <summary>Primitives are hashed from their bits (no string formatting); everything else falls back to the invariant string form.</summary>
        public static ulong AddValue(ulong hash, object value)
            => value switch
            {
                int i => Add(hash ^ 0x01, (ulong)(uint)i),
                long l => Add(hash ^ 0x02, (ulong)l),
                bool b => Add(hash ^ 0x03, b ? 1UL : 0UL),
                Guid g => AddGuid(hash ^ 0x04, g),
                DateTime dt => Add(hash ^ 0x05, (ulong)dt.Ticks),
                DateTimeOffset dto => Add(Add(hash ^ 0x06, (ulong)dto.UtcTicks), (ulong)dto.Offset.Ticks),
                decimal d => AddDecimal(hash ^ 0x07, d),
                double db => Add(hash ^ 0x08, (ulong)BitConverter.DoubleToInt64Bits(db)),
                float f => Add(hash ^ 0x09, (ulong)BitConverter.SingleToInt32Bits(f)),
                short sh => Add(hash ^ 0x0A, (ulong)(ushort)sh),
                byte by => Add(hash ^ 0x0B, by),
                string s => Add(hash, s),
                _ => Add(hash, ValueToString(value)),
            };

        private static ulong AddGuid(ulong hash, Guid guid)
        {
            Span<byte> bytes = stackalloc byte[16];
            guid.TryWriteBytes(bytes);
            foreach (var b in bytes)
            {
                hash = (hash ^ b) * Prime;
            }

            return (hash ^ 0x1F) * Prime;
        }

        private static ulong AddDecimal(ulong hash, decimal value)
        {
            Span<int> bits = stackalloc int[4];
            decimal.GetBits(value, bits);
            foreach (var part in bits)
            {
                hash = Add(hash, (ulong)(uint)part);
            }

            return hash;
        }
    }

    /// <summary>Elements in a collection parameter: provider arrays (Npgsql) or the JSON array EF Core 8+ sends for OPENJSON/json_each.</summary>
    private static int? CollectionElementCount(object? value)
    {
        switch (value)
        {
            case null or DBNull or string { Length: 0 } or byte[]:
                return null;
            case string s when s.Length >= 2 && s[0] == '[' && s[^1] == ']':
                try
                {
                    var reader = new System.Text.Json.Utf8JsonReader(Encoding.UTF8.GetBytes(s));
                    if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.StartArray)
                    {
                        return null;
                    }

                    var n = 0;
                    while (reader.Read() && reader.TokenType != System.Text.Json.JsonTokenType.EndArray)
                    {
                        n++;
                        reader.TrySkip();
                    }

                    return n;
                }
                catch (System.Text.Json.JsonException)
                {
                    return null;
                }
            case string:
                return null;
            case System.Collections.ICollection c:
                return c.Count;
            default:
                return null;
        }
    }

    /// <summary>Largest number of items inside an inline <c>IN (a, b, c)</c> list, or <c>null</c> when there is none.</summary>
    private static int? InlineListCount(string shape)
    {
        int? max = null;
        var idx = 0;
        while ((idx = shape.IndexOf(" IN (", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var start = idx + 5;
            var depth = 1;
            var items = 1;
            var i = start;
            for (; i < shape.Length && depth > 0; i++)
            {
                var ch = shape[i];
                if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    depth--;
                }
                else if (ch == ',' && depth == 1)
                {
                    items++;
                }
            }

            var body = shape[start..Math.Max(start, i - 1)].Trim();
            if (body.Length > 0 && !body.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && (max is null || items > max))
            {
                max = items;
            }

            idx = i;
        }

        return max;
    }

    private static string ValueToString(object value)
        => value switch
        {
            byte[] bytes => "0x" + Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 64))) + "/" + bytes.Length.ToString(CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };

    public static QuerySource MapSource(CommandSource source)
        => source switch
        {
            CommandSource.LinqQuery => QuerySource.Linq,
            CommandSource.ExecuteDelete => QuerySource.Linq,
            CommandSource.ExecuteUpdate => QuerySource.Linq,
            CommandSource.FromSqlQuery => QuerySource.Raw,
            CommandSource.ExecuteSqlRaw => QuerySource.Raw,
            CommandSource.SaveChanges => QuerySource.SaveChanges,
            _ => QuerySource.Other,
        };
}
