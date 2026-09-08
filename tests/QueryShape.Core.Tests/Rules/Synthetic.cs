using Microsoft.EntityFrameworkCore.Diagnostics;
using QueryShape.Normalization;

namespace QueryShape.Core.Tests.Rules;

/// <summary>Builds scopes by hand so rules can be unit-tested without a database.</summary>
internal static class Synthetic
{
    public static QueryShapeScope Scope(Action<QueryShapeOptions>? configure = null)
    {
        var options = new QueryShapeOptions();
        configure?.Invoke(options);
        return QueryShapeScope.Begin("synthetic", options);
    }

    public static CapturedCommand Add(
        this QueryShapeScope scope,
        string sql,
        string parameterHash = "000000000000",
        QuerySource source = QuerySource.Linq,
        QueryInfo? query = null,
        int? rows = null,
        double ms = 1,
        CallSite? callSite = null,
        DbCommandMethod method = DbCommandMethod.ExecuteReader)
    {
        var normalized = SqlNormalizer.Normalize(sql);
        var cmd = new CapturedCommand
        {
            CommandText = sql,
            Shape = normalized.Shape,
            Fingerprint = normalized.Fingerprint,
            Tags = normalized.Tags,
            Source = source,
            CommandSource = source == QuerySource.Linq ? CommandSource.LinqQuery : CommandSource.Unknown,
            ExecuteMethod = method,
            ParameterHash = parameterHash,
            Duration = TimeSpan.FromMilliseconds(ms),
            Query = query,
            CallSite = callSite,
            CommandId = Guid.NewGuid(),
            StartTime = DateTimeOffset.UtcNow,
        };
        cmd.RowsReturned = rows;
        cmd.IsTracking = query is null ? null : query.ReturnsEntities && query.IsTracking;   // as if this context compiled the query
        scope.Record(cmd).Should().BeTrue();
        return cmd;
    }

    public static QueryInfo Query(
        string expression,
        string root,
        bool hasFilter = false,
        bool hasLimit = false,
        bool tracking = true,
        IReadOnlyList<KeyFilter>? keyFilters = null,
        IReadOnlyList<ClientEvaluatedCall>? clientCalls = null,
        IReadOnlyList<string>? collectionIncludes = null,
        bool hasOrdering = false,
        bool hasProjection = false,
        bool hasGrouping = false,
        IReadOnlyList<string>? operators = null)
        => new()
        {
            Expression = expression,
            ExpressionHash = Fingerprint.Compute(expression),
            RootEntityType = "Test." + root,
            RootEntityShortName = root,
            RootTableName = root + "s",
            RootKeyProperties = ["Id"],
            ResultType = "Test." + root,
            ReturnsEntities = true,
            IsTracking = tracking,
            HasFilter = hasFilter,
            HasLimit = hasLimit,
            HasOrdering = hasOrdering,
            HasProjection = hasProjection,
            HasGrouping = hasGrouping,
            Operators = operators ?? [],
            KeyFilters = keyFilters ?? [],
            ClientEvaluatedCalls = clientCalls ?? [],
            CollectionIncludes = collectionIncludes ?? [],
        };
}
