namespace QueryShape;

/// <summary>How serious a diagnosis is. Ordered so that comparisons such as <c>severity &gt;= Severity.Warning</c> work.</summary>
public enum Severity
{
    /// <summary>Worth knowing, never blocks.</summary>
    Info = 0,

    /// <summary>Likely to become a problem as data grows.</summary>
    Warning = 1,

    /// <summary>A real defect: slow today, or dangerous.</summary>
    Error = 2,
}
