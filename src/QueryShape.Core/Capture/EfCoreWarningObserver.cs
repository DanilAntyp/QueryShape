using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using QueryShape.Internal;

namespace QueryShape.Capture;

/// <summary>
/// Listens to EF Core's DiagnosticSource for the compile-time query warnings Microsoft already computes
/// (row limiting without OrderBy, First without OrderBy, multiple collection includes, Distinct after OrderBy)
/// and attaches them to the query being compiled. Free signal; costs nothing when the events do not fire.
/// </summary>
internal sealed class EfCoreWarningObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private const string EfCoreListenerName = "Microsoft.EntityFrameworkCore";

    private static readonly Dictionary<string, string> s_watched = new(StringComparer.Ordinal)
    {
        [CoreEventId.RowLimitingOperationWithoutOrderByWarning.Name!] = "RowLimitingOperationWithoutOrderBy",
        [CoreEventId.FirstWithoutOrderByAndFilterWarning.Name!] = "FirstWithoutOrderByAndFilter",
        [CoreEventId.DistinctAfterOrderByWithoutRowLimitingOperatorWarning.Name!] = "DistinctAfterOrderByWithoutRowLimitingOperator",
        [RelationalEventId.MultipleCollectionIncludeWarning.Name!] = "MultipleCollectionInclude",
    };

    private static int s_subscribed;

    private readonly ExpressionCorrelator _correlator;

    private EfCoreWarningObserver(ExpressionCorrelator correlator)
    {
        _correlator = correlator;
    }

    /// <summary>Subscribes once per process.</summary>
    public static void EnsureSubscribed(ExpressionCorrelator correlator)
    {
        if (Interlocked.Exchange(ref s_subscribed, 1) == 1)
        {
            return;
        }

        try
        {
            DiagnosticListener.AllListeners.Subscribe(new EfCoreWarningObserver(correlator));
        }
        catch (Exception ex)
        {
            Log.Swallowed(null, "diagnostic subscription", ex);
        }
    }

    /// <summary>Short names of the warnings observed (for tests and docs).</summary>
    public static IReadOnlyCollection<string> WatchedWarnings => s_watched.Values;

    /// <summary>Fired when EF Core has translated and planned a query: the compilation that started with <c>QueryCompilationStarting</c> succeeded (ADR-0002).</summary>
    private static readonly string s_planned = CoreEventId.QueryExecutionPlanned.Name!;

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name == EfCoreListenerName)
        {
            listener.Subscribe(this, static name => s_watched.ContainsKey(name) || name == s_planned);
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
    {
        try
        {
            var context = (value.Value as DbContextEventData)?.Context;
            if (value.Key == s_planned)
            {
                _correlator.OnPlanned(context);
                return;
            }

            if (!s_watched.TryGetValue(value.Key, out var shortName))
            {
                return;
            }

            _correlator.OnWarning(context, shortName);
        }
        catch (Exception ex)
        {
            Log.Swallowed(null, "diagnostic event", ex);
        }
    }

    void IObserver<DiagnosticListener>.OnCompleted()
    {
    }

    void IObserver<DiagnosticListener>.OnError(Exception error)
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnCompleted()
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error)
    {
    }
}
