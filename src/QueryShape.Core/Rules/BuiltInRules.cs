namespace QueryShape.Rules;

/// <summary>Factory for the built-in rule set.</summary>
public static class BuiltInRules
{
    /// <summary>New instances of every built-in rule, in id order.</summary>
    public static IReadOnlyList<IRule> CreateAll()
        =>
        [
            new NPlusOneRule(),
            new CartesianExplosionRule(),
            new ClientEvaluationRule(),
            new UnboundedResultSetRule(),
            new TrackingOnReadOnlyQueryRule(),
            new MissingSplitQueryRule(),
            new ContainsLargeCollectionRule(),
            new DuplicateQueryRule(),
            new QueryInLoopRule(),
            new RawSqlConcatenationRule(),
            new RowLimitingWithoutOrderByRule(),
        ];
}
