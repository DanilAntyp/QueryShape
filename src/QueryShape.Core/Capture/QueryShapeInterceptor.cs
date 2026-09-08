using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using QueryShape.Internal;

namespace QueryShape.Capture;

/// <summary>
/// The EF Core interceptor that does the capturing. Register it with <see cref="QueryShapeDbContextOptionsBuilderExtensions.UseQueryShape(DbContextOptionsBuilder, QueryShapeOptions?)"/>.
/// There is exactly one instance (<see cref="Instance"/>): EF Core builds a new internal service provider for every distinct singleton
/// interceptor instance, so per-context options travel in the <see cref="DbContextOptions"/> instead. Never throws out of an interceptor method.
/// </summary>
public sealed class QueryShapeInterceptor : IQueryExpressionInterceptor, IDbCommandInterceptor, ISaveChangesInterceptor
{
    private readonly CommandCapturer _capturer = new();

    private QueryShapeInterceptor()
    {
        EfCoreWarningObserver.EnsureSubscribed(_capturer.Correlator);
    }

    /// <summary>The process-wide interceptor.</summary>
    public static QueryShapeInterceptor Instance { get; } = new();

    /// <summary>Commands that ran while no <see cref="QueryShapeScope"/> was current (bounded ring buffer).</summary>
    public IReadOnlyList<CapturedCommand> RecentUnscopedCommands => _capturer.RecentUnscopedCommands;

    /// <summary>Options in effect for a context (from <c>UseQueryShape(options)</c>, else <see cref="QueryShapeOptions.Default"/>).</summary>
    public QueryShapeOptions OptionsFor(DbContext context) => _capturer.OptionsFor(context);

    internal CommandCapturer Capturer => _capturer;

    // ---- IQueryExpressionInterceptor ----

    /// <inheritdoc />
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        try
        {
            if (!_capturer.OptionsFor(eventData.Context).Enabled)
            {
                return queryExpression;
            }

            var info = QueryExpressionAnalyzer.Analyze(queryExpression, eventData.Context, eventData.ExpressionPrinter);
            _capturer.Correlator.OnCompiled(eventData.Context, info);
        }
        catch (Exception ex)
        {
            Log.Swallowed(_capturer.OptionsFor(eventData.Context), "query compilation", ex);
        }

        return queryExpression;
    }

    // ---- IDbCommandInterceptor ----

    /// <inheritdoc />
    public InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Starting(command, eventData);
        return result;
    }

    /// <inheritdoc />
    public InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Starting(command, eventData);
        return result;
    }

    /// <inheritdoc />
    public InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Starting(command, eventData);
        return result;
    }

    /// <inheritdoc />
    public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Starting(command, eventData);
        return new ValueTask<InterceptionResult<DbDataReader>>(result);
    }

    /// <inheritdoc />
    public ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Starting(command, eventData);
        return new ValueTask<InterceptionResult<object>>(result);
    }

    /// <inheritdoc />
    public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Starting(command, eventData);
        return new ValueTask<InterceptionResult<int>>(result);
    }

    private void Starting(DbCommand command, CommandEventData eventData)
        => _capturer.Starting(
            _capturer.OptionsFor(eventData.Context),
            command,
            CommandCapturer.MapSource(eventData.CommandSource),
            eventData.CommandId,
            eventData.StartTime);

    /// <inheritdoc />
    public DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        var captured = Capture(command, eventData, null, null);
        return Wrap(result, eventData.CommandId, captured);
    }

    /// <inheritdoc />
    public object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Capture(command, eventData, result, null);
        return result;
    }

    /// <inheritdoc />
    public int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Capture(command, eventData, result, null);
        return result;
    }

    /// <inheritdoc />
    public ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        var captured = Capture(command, eventData, null, null);
        return new ValueTask<DbDataReader>(Wrap(result, eventData.CommandId, captured));
    }

    /// <summary>Row counts come from our own wrapper: EF Core's <c>ReadCount</c> counts calls, including the final <c>false</c>.</summary>
    private DbDataReader Wrap(DbDataReader reader, Guid commandId, CapturedCommand? captured)
    {
        if (captured is null)
        {
            return reader; // disabled or capture failed: never wrap for nothing
        }

        try
        {
            var trackRoots = captured?.Query is { CollectionIncludes.Count: >= 2, SplittingBehavior: "SingleQuery" };
            return new CountingDataReader(reader, (rows, affected, roots) => _capturer.ReaderClosed(commandId, rows, affected, roots), trackRoots);
        }
        catch (Exception ex)
        {
            Log.Swallowed(null, "reader wrap", ex);
            return reader;
        }
    }

    /// <inheritdoc />
    public ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Capture(command, eventData, result, null);
        return new ValueTask<object?>(result);
    }

    /// <inheritdoc />
    public ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Capture(command, eventData, result, null);
        return new ValueTask<int>(result);
    }

    /// <inheritdoc />
    public void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        => Capture(command, eventData, null, eventData.Exception);

    /// <inheritdoc />
    public Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Capture(command, eventData, null, eventData.Exception);
        return Task.CompletedTask;
    }

    private CapturedCommand? Capture(DbCommand command, CommandEndEventData eventData, object? result, Exception? error)
        => _capturer.Capture(
            _capturer.OptionsFor(eventData.Context),
            command,
            CommandCapturer.MapSource(eventData.CommandSource),
            eventData.CommandSource,
            eventData.ExecuteMethod,
            eventData.CommandId,
            eventData.ConnectionId,
            eventData.Context,
            eventData.StartTime,
            eventData.Duration,
            eventData.IsAsync,
            result,
            error);

    // ---- ISaveChangesInterceptor ----
    // Nothing runs before the save on purpose: calling ChangeTracker.Entries() there would run DetectChanges a second time (EF Core runs its own
    // right after this interceptor). The write set is observed through change-tracker events instead (see CommandCapturer).

    /// <inheritdoc />
    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Completed(eventData.Context, result);
        return result;
    }

    /// <inheritdoc />
    public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Completed(eventData.Context, result);
        return new ValueTask<int>(result);
    }

    /// <inheritdoc />
    public void SaveChangesFailed(DbContextErrorEventData eventData) => Completed(eventData.Context, 0);

    /// <inheritdoc />
    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Completed(eventData.Context, 0);
        return Task.CompletedTask;
    }

    private void Completed(DbContext? context, int written)
    {
        if (context is not null)
        {
            _capturer.SaveChangesCompleted(context, written);
        }
    }
}
