namespace QueryShape;

/// <summary>What a LINQ query filters on: a foreign key or primary key compared against a parameter.</summary>
/// <param name="EntityType">CLR name of the entity being filtered (the query root).</param>
/// <param name="PropertyName">The key/foreign-key property compared to a parameter.</param>
/// <param name="IsPrimaryKey"><c>true</c> when the property is (part of) the primary key rather than a foreign key.</param>
/// <param name="RelatedEntityType">For a foreign key: the principal entity type. For a primary key: <c>null</c>.</param>
/// <param name="NavigationOnRelated">Navigation from the principal back to this entity (e.g. <c>Customer.Orders</c>), if one exists.</param>
/// <param name="NavigationToRelated">Navigation from this entity to the principal (e.g. <c>Order.Customer</c>), if one exists.</param>
/// <param name="IsSolePredicate">
/// <c>true</c> when this comparison is the whole predicate of the query: the only <c>Where</c>/<c>First(...)</c> lambda, and nothing else in it
/// (no <c>&amp;&amp;</c>). Only then does reading the navigation return exactly what the query returned.
/// </param>
public sealed record KeyFilter(
    string EntityType,
    string PropertyName,
    bool IsPrimaryKey,
    string? RelatedEntityType,
    string? NavigationOnRelated,
    string? NavigationToRelated,
    bool IsSolePredicate = false);

/// <summary>A method call in the expression tree that EF Core cannot translate and will run on the client.</summary>
/// <param name="Method">Display name, e.g. <c>PriceFormatter.Format(decimal)</c>.</param>
/// <param name="DeclaringType">Full name of the declaring type.</param>
/// <param name="Operator">The LINQ operator whose lambda contains the call (e.g. <c>Select</c>).</param>
public sealed record ClientEvaluatedCall(string Method, string DeclaringType, string Operator);

/// <summary>Facts derived from the LINQ expression tree at compile time. This is the link from SQL back to C#.</summary>
public sealed class QueryInfo
{
    /// <summary>The expression as printed by EF Core's <c>ExpressionPrinter</c>. Parameters appear by name, never by value.</summary>
    public required string Expression { get; init; }

    /// <summary>SHA-256 of <see cref="Expression"/>, first 12 hex characters.</summary>
    public required string ExpressionHash { get; init; }

    /// <summary>CLR name of the entity the query starts from, or <c>null</c> for non-entity roots.</summary>
    public string? RootEntityType { get; init; }

    /// <summary>Short (unqualified) CLR name of the root entity, convenient for messages.</summary>
    public string? RootEntityShortName { get; init; }

    /// <summary>Table (or view) the root entity maps to, when known.</summary>
    public string? RootTableName { get; init; }

    /// <summary>Primary key property names of the root entity, when known.</summary>
    public IReadOnlyList<string> RootKeyProperties { get; init; } = [];

    /// <summary>The element type of the result (for a scalar query, the scalar type).</summary>
    public string? ResultType { get; init; }

    /// <summary><c>true</c> when the query materializes entity instances the change tracker will track.</summary>
    public bool IsTracking { get; init; }

    /// <summary>
    /// <c>true</c> when the query itself calls <c>AsTracking</c>/<c>AsNoTracking</c>(<c>WithIdentityResolution</c>), so the tracking behavior is a
    /// decision written at the call site; <c>false</c> when it is inherited from the context default.
    /// </summary>
    public bool TrackingIsExplicit { get; init; }

    /// <summary><c>true</c> when the result element type is an entity type (as opposed to a projection or scalar).</summary>
    public bool ReturnsEntities { get; init; }

    /// <summary><c>true</c> when there is a <c>Where</c> or a predicate overload of <c>First/Single/Any/Count/Last</c>.</summary>
    public bool HasFilter { get; init; }

    /// <summary><c>true</c> when a row-limiting or aggregating operator is present (<c>Take</c>, <c>First</c>, <c>Any</c>, <c>Count</c>...).</summary>
    public bool HasLimit { get; init; }

    /// <summary><c>true</c> when the final operator is a projection (<c>Select</c>).</summary>
    public bool HasProjection { get; init; }

    /// <summary><c>true</c> when an <c>OrderBy</c>/<c>ThenBy</c> is present.</summary>
    public bool HasOrdering { get; init; }

    /// <summary><c>true</c> when a <c>GroupBy</c> is present: the result rows are groups, not rows of the root table.</summary>
    public bool HasGrouping { get; init; }

    /// <summary>Top-level LINQ operators in source order (<c>Where</c>, <c>Include</c>, <c>OrderBy</c>, <c>First</c>...). Terminal materializers such as <c>ToListAsync</c> are not part of the expression.</summary>
    public IReadOnlyList<string> Operators { get; init; } = [];

    /// <summary>Effective query splitting behavior (from <c>AsSplitQuery</c>/<c>AsSingleQuery</c> or the context default).</summary>
    public string SplittingBehavior { get; init; } = "SingleQuery";

    /// <summary>
    /// <c>true</c> when the query itself calls <c>AsSplitQuery</c>/<c>AsSingleQuery</c>, so <see cref="SplittingBehavior"/> is a decision someone
    /// wrote at the call site; <c>false</c> when it is inherited from the context default. The last such call in the chain wins, as in EF Core.
    /// </summary>
    public bool SplittingIsExplicit { get; init; }

    /// <summary>Distinct collection navigations loaded via <c>Include</c>/<c>ThenInclude</c>.</summary>
    public IReadOnlyList<string> CollectionIncludes { get; init; } = [];

    /// <summary>CLR names of every entity type loaded through <c>Include</c>/<c>ThenInclude</c> (reference and collection navigations), sorted.</summary>
    public IReadOnlyList<string> IncludedEntityTypes { get; init; } = [];

    /// <summary>Tags added with <c>TagWith</c>.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Key and foreign-key filters compared against parameters (the N+1 signature).</summary>
    public IReadOnlyList<KeyFilter> KeyFilters { get; init; } = [];

    /// <summary>Calls EF Core will evaluate on the client.</summary>
    public IReadOnlyList<ClientEvaluatedCall> ClientEvaluatedCalls { get; init; } = [];

    /// <summary><c>true</c> when the root is <c>FromSql</c>/<c>FromSqlRaw</c>.</summary>
    public bool IsFromSql { get; init; }

    /// <summary><c>true</c> when the query is an <c>ExecuteDelete</c>/<c>ExecuteUpdate</c>.</summary>
    public bool IsBulkOperation { get; init; }

    /// <summary><c>true</c> when the query filters with <c>Contains</c> over a parameterized collection.</summary>
    public bool HasParameterCollectionContains { get; init; }

    /// <summary>Names of the query parameters EF Core extracted from the expression (<c>__id_0</c> in EF Core 8, <c>id</c> in EF Core 10). They reappear as the SQL parameter names.</summary>
    public IReadOnlyList<string> ParameterNames { get; init; } = [];

    /// <summary><c>true</c> once EF Core reported the compilation as planned (translated successfully). Set from EF Core's <c>QueryExecutionPlanned</c> event.</summary>
    internal bool Planned { get; set; }

    private readonly List<string> _warnings = [];

    /// <summary>
    /// EF Core's own compile-time warnings for this query, by short name: <c>RowLimitingOperationWithoutOrderBy</c>, <c>FirstWithoutOrderByAndFilter</c>,
    /// <c>MultipleCollectionInclude</c>, <c>DistinctAfterOrderByWithoutRowLimitingOperator</c>. Collected through EF Core's DiagnosticSource.
    /// </summary>
    public IReadOnlyList<string> Warnings => _warnings;

    internal void AddWarning(string warning)
    {
        lock (_warnings)
        {
            if (!_warnings.Contains(warning, StringComparer.Ordinal))
            {
                _warnings.Add(warning);
            }
        }
    }

    /// <summary>Renders the expression on one line, trimmed for messages.</summary>
    public override string ToString() => Expression;
}
