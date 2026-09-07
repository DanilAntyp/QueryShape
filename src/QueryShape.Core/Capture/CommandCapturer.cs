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
    private readonly object _gate = new();
    private readonly Queue<CapturedCommand> _unscoped = new();
    private readonly ConditionalWeakTable<DbContext, QueryShapeOptions> _optionsByContext = new();
    private int _unscopedSequence;

    public ExpressionCorrelator Correlator { get; } = new();

    /// <summary>Options for a context: from its <see cref="QueryShapeOptionsExtension"/>, else <see cref="QueryShapeOptions.Default"/>.</summary>
    public QueryShapeOptions OptionsFor(DbContext? context)
    {
        if (context is null)
        {
            return QueryShapeOptions.Default;
        }

        if (_optionsByContext.TryGetValue(context, out var cached))
        {
            return cached;
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

        _optionsByContext.AddOrUpdate(context, resolved);
        return resolved;
    }

    public IReadOnlyList<CapturedCommand> RecentUnscopedCommands
    {
        get { lock (_gate) { return _unscoped.ToArray(); } }
    }

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
        try
        {
            var normalized = SqlNormalizer.Normalize(command.CommandText ?? string.Empty);
            var scope = QueryShapeScope.Current;

            QueryInfo? query = null;
            if (commandSource is CommandSource.LinqQuery or CommandSource.FromSqlQuery or CommandSource.ExecuteUpdate or CommandSource.ExecuteDelete)
            {
                query = Correlator.Resolve(context, normalized.Fingerprint, normalized.Shape);
            }

            // Tags: the expression tree has them exactly; the SQL comment block is the fallback (EF Core joins tags into one block).
            var tags = query is { Tags.Count: > 0 } ? query.Tags : normalized.Tags;

            var wantCallSite = options.CaptureCallSites || (scope?.Options.CaptureCallSites ?? false);
            var callSite = CallSiteCapture.FromTags(tags) ?? (wantCallSite ? CallSiteCapture.Capture() : null);

            var parameters = CaptureParameters(command, options.IncludeParameterValues, out var parameterHash, out var maxCollectionCount);
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
                ProviderName = SafeProviderName(context),
                Query = query,
                CallSite = callSite,
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

            var listeners = scope?.Options.Listeners.Count > 0 && !ReferenceEquals(scope.Options, options)
                ? options.Listeners.Concat(scope.Options.Listeners).Distinct()
                : options.Listeners;
            foreach (var listener in listeners)
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

    public void ReaderClosed(Guid commandId, int readCount, int recordsAffected, int? distinctRoots = null)
    {
        try
        {
            var scope = QueryShapeScope.Current;
            var command = scope?.FindByCommandId(commandId);
            if (command is null)
            {
                lock (_gate)
                {
                    command = _unscoped.LastOrDefault(c => c.CommandId == commandId);
                }
            }

            if (command is null)
            {
                return;
            }

            command.RowsReturned = readCount;
            command.DistinctRootsEstimate = distinctRoots;
            if (recordsAffected >= 0 && command.RowsAffected is null && command.ExecuteMethod != DbCommandMethod.ExecuteReader)
            {
                command.RowsAffected = recordsAffected;
            }

            scope?.InvalidateAnalysis();
        }
        catch (Exception ex)
        {
            Log.Swallowed(null, "reader closed", ex);
        }
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

    private static IReadOnlyList<CapturedParameter> CaptureParameters(DbCommand command, bool includeValues, out string parameterHash, out int? maxCollectionCount)
    {
        maxCollectionCount = null;
        var count = command.Parameters.Count;
        if (count == 0)
        {
            parameterHash = "000000000000";
            return [];
        }

        var list = new CapturedParameter[count];
        var hashInput = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var p = command.Parameters[i];
            var value = p.Value;
            var isNull = value is null || value is DBNull;
            var text = isNull ? "NULL" : ValueToString(value!);
            hashInput.Append(p.ParameterName).Append('=').Append(text).Append(';');
            list[i] = new CapturedParameter(p.ParameterName, p.DbType, p.Direction, includeValues && !isNull ? value : null, isNull);

            var elements = CollectionElementCount(value);
            if (elements is { } n && (maxCollectionCount is null || n > maxCollectionCount))
            {
                maxCollectionCount = n;
            }
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(hashInput.ToString()), hash);
        parameterHash = Convert.ToHexString(hash[..6]).ToLowerInvariant();
        return list;
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
