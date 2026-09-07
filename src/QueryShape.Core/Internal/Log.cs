using Microsoft.Extensions.Logging;

namespace QueryShape.Internal;

/// <summary>Debug-level logging for QueryShape's own failures. Never throws; never logs SQL parameter values.</summary>
internal static class Log
{
    private static long s_errorCount;

    /// <summary>Total number of swallowed internal errors since process start. Exposed for tests and health checks.</summary>
    public static long ErrorCount => Interlocked.Read(ref s_errorCount);

    public static void Swallowed(QueryShapeOptions? options, string where, Exception ex)
    {
        Interlocked.Increment(ref s_errorCount);
        try
        {
            options?.LoggerFactory?.CreateLogger("QueryShape").LogDebug(ex, "QueryShape swallowed an exception in {Where}", where);
        }
        catch
        {
            // Logging must never fail the caller.
        }
    }

    public static void RuleFailed(QueryShapeOptions options, string ruleId, Exception ex)
        => Swallowed(options, "rule " + ruleId, ex);

    public static void Info(QueryShapeOptions? options, string message)
    {
        try
        {
            options?.LoggerFactory?.CreateLogger("QueryShape").LogInformation("{Message}", message);
        }
        catch
        {
            // Logging must never fail the caller.
        }
    }
}
