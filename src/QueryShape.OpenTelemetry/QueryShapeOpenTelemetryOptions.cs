namespace QueryShape.OpenTelemetry;

/// <summary>Knobs for the OpenTelemetry enrichment.</summary>
public sealed class QueryShapeOpenTelemetryOptions
{
    /// <summary>Name of the <c>ActivitySource</c> and <c>Meter</c>.</summary>
    public const string SourceName = "QueryShape";

    /// <summary><c>queryshape.shape</c> is truncated to this many characters. Default 1024.</summary>
    public int MaxShapeLength { get; set; } = 1024;

    /// <summary>Add the <c>fingerprint</c> tag to the <c>queryshape.queries</c> counter. Off by default: one series per distinct query is high cardinality.</summary>
    public bool IncludeFingerprintInMetrics { get; set; }

    /// <summary>
    /// When the ambient activity is not a database span (no <c>db.system</c> tag, e.g. SQLite under an ASP.NET Core request span),
    /// record each command as a <c>queryshape.query</c> event instead of overwriting tags. Default on. See ADR-0007.
    /// </summary>
    public bool EmitQueryEvents { get; set; } = true;

    /// <summary>Emit metrics through the <c>QueryShape</c> meter. Default on.</summary>
    public bool EmitMetrics { get; set; } = true;
}
