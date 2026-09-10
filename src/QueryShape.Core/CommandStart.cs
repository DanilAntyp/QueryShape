namespace QueryShape;

/// <summary>
/// What is known about a command before it executes: enough to mark the provider's span while that span is still current.
/// EF Core raises its <c>CommandExecuted</c> diagnostic event (which OpenTelemetry instrumentations use to stop their span) before it calls the
/// <c>Executed</c> interceptor, so anything that must land on the database span has to be attached from the <c>Executing</c> side.
/// </summary>
/// <param name="CommandId">EF Core's command id; the same id reaches <see cref="IQueryShapeListener.OnCommandCaptured"/> on the <see cref="CapturedCommand"/>.</param>
/// <param name="Fingerprint">Fingerprint of the shape.</param>
/// <param name="Shape">Normalized SQL.</param>
/// <param name="Source">Where the command came from.</param>
/// <param name="Tags">Tags from the SQL comment block (<c>TagWith</c>).</param>
/// <param name="CallSite">First user-code frame, when known.</param>
/// <param name="CallSiteOrigin">How the call site was obtained.</param>
/// <param name="StartTime">When execution started.</param>
public sealed record CommandStart(
    Guid CommandId,
    string Fingerprint,
    string Shape,
    QuerySource Source,
    IReadOnlyList<string> Tags,
    CallSite? CallSite,
    CallSiteOrigin CallSiteOrigin,
    DateTimeOffset StartTime)
{
    /// <summary>User-code frames the command was reached through, innermost first; empty unless <see cref="QueryShapeOptions.CallPathDepth"/> asks for more than one.</summary>
    public IReadOnlyList<CallSite> CallPath { get; init; } = [];
}
