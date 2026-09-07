namespace QueryShape;

/// <summary>Receives capture events. Used by the OpenTelemetry package; available to anyone who wants to ship captures elsewhere.</summary>
/// <remarks>Implementations must not throw and must be cheap: they run inside the interceptor on the query path.</remarks>
public interface IQueryShapeListener
{
    /// <summary>Called after a command finished (or failed), before it is attached to the scope's list.</summary>
    void OnCommandCaptured(CapturedCommand command, QueryShapeScope? scope);

    /// <summary>Called when a scope is disposed, with the diagnoses its rules produced.</summary>
    void OnScopeCompleted(QueryShapeScope scope, IReadOnlyList<Diagnosis> diagnoses);
}
