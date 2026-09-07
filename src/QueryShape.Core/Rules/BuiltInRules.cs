namespace QueryShape.Rules;

/// <summary>Factory for the built-in rule set.</summary>
public static class BuiltInRules
{
    /// <summary>New instances of every built-in rule, in id order.</summary>
    public static IReadOnlyList<IRule> CreateAll()
        =>
        [
            new NPlusOneRule(),
            new ClientEvaluationRule(),
            new UnboundedResultSetRule(),
            new DuplicateQueryRule(),
        ];
}
