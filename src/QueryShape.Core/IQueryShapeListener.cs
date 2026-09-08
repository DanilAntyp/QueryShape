namespace QueryShape;

/// <summary>Receives capture events. Used by the OpenTelemetry package; available to anyone who wants to ship captures elsewhere.</summary>
/// <remarks>Implementations must not throw and must be cheap: they run inside the interceptor on the query path.</remarks>
public interface IQueryShapeListener
{
    /// <summary>
    /// Called right before a command executes, with what is already known about it. This is the only moment the database provider's own span
    /// is guaranteed to be <c>Activity.Current</c>; by <see cref="OnCommandCaptured"/> it may already be stopped. Only raised when listeners are registered.
    /// </summary>
    void OnCommandExecuting(CommandStart start, QueryShapeScope? scope)
    {
    }

    /// <summary>Called after a command finished (or failed), once it is attached to the scope's list (its <see cref="CapturedCommand.Sequence"/> is set).</summary>
    void OnCommandCaptured(CapturedCommand command, QueryShapeScope? scope);

    /// <summary>Called when a scope is disposed, with the diagnoses its rules produced.</summary>
    void OnScopeCompleted(QueryShapeScope scope, IReadOnlyList<Diagnosis> diagnoses);
}
