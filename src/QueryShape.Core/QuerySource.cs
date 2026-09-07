namespace QueryShape;

/// <summary>Where a captured command came from.</summary>
public enum QuerySource
{
    /// <summary>Produced by EF Core translating a LINQ query (including <c>ExecuteDelete</c>/<c>ExecuteUpdate</c>).</summary>
    Linq = 0,

    /// <summary>Hand-written SQL: <c>FromSqlRaw</c>, <c>ExecuteSqlRaw</c>, Dapper or a raw <c>DbCommand</c>.</summary>
    Raw = 1,

    /// <summary>Emitted by <c>SaveChanges</c>.</summary>
    SaveChanges = 2,

    /// <summary>Migrations, value generators, scaffolding and anything else EF Core runs on its own behalf.</summary>
    Other = 3,
}
