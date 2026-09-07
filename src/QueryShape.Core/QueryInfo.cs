namespace QueryShape;

/// <summary>What a LINQ query filters on: a foreign key or primary key compared against a parameter.</summary>
/// <param name="EntityType">CLR name of the entity being filtered (the query root).</param>
/// <param name="PropertyName">The key/foreign-key property compared to a parameter.</param>
/// <param name="IsPrimaryKey"><c>true</c> when the property is (part of) the primary key rather than a foreign key.</param>
/// <param name="RelatedEntityType">For a foreign key: the principal entity type. For a primary key: <c>null</c>.</param>
/// <param name="NavigationOnRelated">Navigation from the principal back to this entity (e.g. <c>Customer.Orders</c>), if one exists.</param>
/// <param name="NavigationToRelated">Navigation from this entity to the principal (e.g. <c>Order.Customer</c>), if one exists.</param>
public sealed record KeyFilter(
    string EntityType,
    string PropertyName,
    bool IsPrimaryKey,
    string? RelatedEntityType,
    string? NavigationOnRelated,
    string? NavigationToRelated);

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

    /// <summary><c>true</c> when the result element type is an entity type (as opposed to a projection or scalar).</summary>
    public bool ReturnsEntities { get; init; }

    /// <summary><c>true</c> when there is a <c>Where</c> or a predicate overload of <c>First/Single/Any/Count/Last</c>.</summary>
    public bool HasFilter { get; init; }

    /// <summary><c>true</c> when a row-limiting or aggregating operator is present (<c>Take</c>, <c>First</c>, <c>Any</c>, <c>Count</c>...).</summary>
    public bool HasLimit { get; init; }

    /// <summary><c>true</c> when the final operator is a projection (<c>Select</c>).</summary>
    public bool HasProjection { get; init; }

    /// <summary>Effective query splitting behavior (from <c>AsSplitQuery</c>/<c>AsSingleQuery</c> or the context default).</summary>
    public string SplittingBehavior { get; init; } = "SingleQuery";

    /// <summary>Distinct collection navigations loaded via <c>Include</c>/<c>ThenInclude</c>.</summary>
    public IReadOnlyList<string> CollectionIncludes { get; init; } = [];

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

    /// <summary>Renders the expression on one line, trimmed for messages.</summary>
    public override string ToString() => Expression;
}
