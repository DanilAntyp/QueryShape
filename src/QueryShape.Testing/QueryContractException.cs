namespace QueryShape.Testing;

/// <summary>A measured contract violation, distinguished from application/test infrastructure failures by CLI runners.</summary>
public sealed class QueryContractException(string message) : InvalidOperationException("QueryShape contract violated: " + message);
