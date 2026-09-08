using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using QueryShape.Normalization;

namespace QueryShape.Capture;

/// <summary>Walks the pre-translation LINQ expression tree and derives the facts rules need.</summary>
internal sealed class QueryExpressionAnalyzer : ExpressionVisitor
{
    private static readonly HashSet<string> s_limitOperators =
    [
        "Take", "First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault",
        "ElementAt", "ElementAtOrDefault", "Any", "All", "Count", "LongCount", "Min", "Max", "Sum", "Average", "Contains",
        "MinBy", "MaxBy",
    ];

    private static readonly HashSet<string> s_predicateOperators =
    [
        "Where", "First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault", "Any", "Count", "LongCount",
    ];

    private static readonly HashSet<string> s_bulkOperators = ["ExecuteDelete", "ExecuteUpdate", "ExecuteDeleteAsync", "ExecuteUpdateAsync"];

    private readonly IModel? _model;
    private readonly HashSet<MethodInfo> _dbFunctions;
    private readonly List<string> _includePaths = [];
    private readonly List<string> _tags = [];
    private readonly List<KeyFilter> _keyFilters = [];
    private readonly List<ClientEvaluatedCall> _clientCalls = [];
    private readonly Stack<string> _operators = new();
    private readonly List<string> _operatorSequence = [];
    private readonly Stack<Expression> _lambdaBodies = new();
    private readonly HashSet<ParameterExpression> _entityLambdaParameters = [];
    private readonly HashSet<string> _parameterNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _includedTypes = new(StringComparer.Ordinal);

    private IEntityType? _root;
    private bool _isFromSql;
    private bool _hasFilter;
    private bool _hasLimit;
    private bool _hasProjection;
    private bool _hasOrdering;
    private bool _hasGrouping;
    private int _predicateLambdas;
    private bool _isBulk;
    private bool _hasParameterCollectionContains;
    private bool? _trackingOverride;
    private string? _splittingOverride;
    private int _lambdaDepth;

    private QueryExpressionAnalyzer(IModel? model)
    {
        _model = model;
        _dbFunctions = model is null ? [] : model.GetDbFunctions().Select(f => f.MethodInfo).OfType<MethodInfo>().ToHashSet();
    }

    public static QueryInfo Analyze(Expression expression, DbContext? context, ExpressionPrinter? printer)
    {
        string printed;
        try
        {
            printed = printer?.PrintExpression(expression) ?? expression.ToString();
        }
        catch
        {
            printed = expression.ToString();
        }

        printed = printed.Replace("\r\n", "\n", StringComparison.Ordinal);
        var analyzer = new QueryExpressionAnalyzer(context?.Model);
        try
        {
            analyzer.Visit(expression);
        }
        catch
        {
            // Partial information is better than none; the visitor is best-effort.
        }

        return analyzer.Build(expression, context, printed);
    }

    private QueryInfo Build(Expression expression, DbContext? context, string printed)
    {
        var elementType = GetElementType(expression.Type);
        var resultEntity = _model?.FindEntityType(elementType);
        var returnsEntities = resultEntity is not null;

        var defaultTracking = context?.ChangeTracker.QueryTrackingBehavior ?? QueryTrackingBehavior.TrackAll;
        var hasKey = resultEntity?.FindPrimaryKey() is not null; // keyless entity types (views, HasNoKey) are never tracked
        var isTracking = returnsEntities && hasKey && (_trackingOverride ?? defaultTracking == QueryTrackingBehavior.TrackAll);

        var splitting = _splittingOverride ?? GetDefaultSplitting(context);

        var collectionIncludes = _includePaths.Distinct(StringComparer.Ordinal).ToArray();

        // A key comparison is the sole predicate only when it is the whole lambda body and that lambda is the query's only predicate.
        var keyFilters = _predicateLambdas > 1
            ? _keyFilters.Select(k => k with { IsSolePredicate = false }).ToArray()
            : _keyFilters.ToArray();

        return new QueryInfo
        {
            Expression = printed,
            ExpressionHash = Fingerprint.Compute(printed),
            RootEntityType = _root?.ClrType.FullName ?? _root?.Name,
            RootEntityShortName = _root?.ClrType.Name ?? _root?.ShortName(),
            RootTableName = _root is null ? null : (_root.GetTableName() ?? _root.GetViewName()),
            RootKeyProperties = _root?.FindPrimaryKey()?.Properties.Select(p => p.Name).ToArray() ?? [],
            ResultType = elementType.FullName ?? elementType.Name,
            IsTracking = isTracking,
            ReturnsEntities = returnsEntities,
            HasFilter = _hasFilter,
            HasLimit = _hasLimit,
            HasProjection = _hasProjection,
            HasOrdering = _hasOrdering,
            HasGrouping = _hasGrouping,
            Operators = _operatorSequence.ToArray(),
            SplittingBehavior = splitting,
            CollectionIncludes = collectionIncludes,
            IncludedEntityTypes = _includedTypes.OrderBy(t => t, StringComparer.Ordinal).ToArray(),
            Tags = _tags.ToArray(),
            KeyFilters = keyFilters,
            ClientEvaluatedCalls = _clientCalls.ToArray(),
            IsFromSql = _isFromSql,
            IsBulkOperation = _isBulk,
            HasParameterCollectionContains = _hasParameterCollectionContains,
            ParameterNames = _parameterNames.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
        };
    }

    private static string GetDefaultSplitting(DbContext? context)
    {
        try
        {
            var options = context?.GetService<IDbContextOptions>();
            var relational = options?.Extensions.OfType<RelationalOptionsExtension>().FirstOrDefault();
            return (relational?.QuerySplittingBehavior ?? QuerySplittingBehavior.SingleQuery).ToString();
        }
        catch
        {
            return nameof(QuerySplittingBehavior.SingleQuery);
        }
    }

    private static Type GetElementType(Type type)
    {
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(IQueryable<>) || def == typeof(IOrderedQueryable<>) || def == typeof(IEnumerable<>)
                || def == typeof(IAsyncEnumerable<>) || def == typeof(IIncludableQueryable<,>))
            {
                return type.GetGenericArguments()[0];
            }

            var queryable = type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQueryable<>));
            if (queryable is not null)
            {
                return queryable.GetGenericArguments()[0];
            }
        }

        return type;
    }

    protected override Expression VisitExtension(Expression node)
    {
        if (node is EntityQueryRootExpression root)
        {
            _root ??= root.EntityType;
            if (node.GetType().Name == "FromSqlQueryRootExpression")
            {
                _isFromSql = true;
            }

            return node;
        }

        // EF Core 10 represents extracted parameters as QueryParameterExpression (name = the SQL parameter name without its prefix).
        if (node.GetType().Name == "QueryParameterExpression" && node.GetType().GetProperty("Name")?.GetValue(node) is string name)
        {
            _parameterNames.Add(name);
        }

        // EF-specific nodes (parameters etc.) have no children we care about.
        return node;
    }

    protected override Expression VisitParameter(ParameterExpression node)
    {
        // EF Core 8 represents extracted parameters as ParameterExpression named __x_0; lambda parameters have plain names.
        if (node.Name is { } name && name.StartsWith("__", StringComparison.Ordinal))
        {
            _parameterNames.Add(name);
        }

        return base.VisitParameter(node);
    }

    protected override Expression VisitInvocation(InvocationExpression node)
    {
        if (_lambdaDepth > 0)
        {
            _clientCalls.Add(new ClientEvaluatedCall("delegate invocation", node.Expression.Type.FullName ?? "delegate", CurrentOperator));
        }

        return base.VisitInvocation(node);
    }

    protected override Expression VisitLambda<T>(Expression<T> node)
    {
        foreach (var p in node.Parameters)
        {
            if (_model?.FindEntityType(p.Type) is not null)
            {
                _entityLambdaParameters.Add(p);
            }
        }

        _lambdaDepth++;
        _lambdaBodies.Push(node.Body);
        try
        {
            return base.VisitLambda(node);
        }
        finally
        {
            _lambdaBodies.Pop();
            _lambdaDepth--;
        }
    }

    protected override Expression VisitBinary(BinaryExpression node)
    {
        if (_lambdaDepth > 0 && node.NodeType == ExpressionType.Equal && _operators.Count > 0 && s_predicateOperators.Contains(CurrentOperator))
        {
            var sole = _lambdaBodies.Count > 0 && ReferenceEquals(_lambdaBodies.Peek(), node);
            TryRecordKeyFilter(node.Left, node.Right, sole);
            TryRecordKeyFilter(node.Right, node.Left, sole);
        }

        return base.VisitBinary(node);
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        var method = node.Method;
        var declaring = method.DeclaringType;

        if (_lambdaDepth == 0 && (declaring == typeof(Queryable) || declaring == typeof(EntityFrameworkQueryableExtensions)
                                  || declaring == typeof(RelationalQueryableExtensions) || declaring?.Namespace == "Microsoft.EntityFrameworkCore"))
        {
            return VisitOperator(node);
        }

        if (_lambdaDepth > 0)
        {
            InspectCallInsideLambda(node);
        }

        return base.VisitMethodCall(node);
    }

    private Expression VisitOperator(MethodCallExpression node)
    {
        // Source chain first, so Include paths and the root are known before this operator is processed.
        if (node.Arguments.Count > 0)
        {
            Visit(node.Arguments[0]);
        }

        var name = node.Method.Name;
        var argCount = node.Arguments.Count;
        _operatorSequence.Add(name);

        switch (name)
        {
            case "Where":
                _hasFilter = true;
                _predicateLambdas++;
                break;
            case "Select":
                _hasProjection = true;
                break;
            case "OrderBy":
            case "OrderByDescending":
            case "ThenBy":
            case "ThenByDescending":
                _hasOrdering = true;
                break;
            case "GroupBy":
                _hasGrouping = true;
                break;
            case "AsNoTracking":
            case "AsNoTrackingWithIdentityResolution":
                _trackingOverride = false;
                break;
            case "AsTracking":
                _trackingOverride = argCount == 1 || !(node.Arguments[1] is ConstantExpression { Value: QueryTrackingBehavior b } && b != QueryTrackingBehavior.TrackAll);
                break;
            case "AsSplitQuery":
                _splittingOverride = nameof(QuerySplittingBehavior.SplitQuery);
                break;
            case "AsSingleQuery":
                _splittingOverride = nameof(QuerySplittingBehavior.SingleQuery);
                break;
            case "TagWith":
                if (argCount > 1 && node.Arguments[1] is ConstantExpression { Value: string tag })
                {
                    _tags.Add(tag);
                }

                break;
            case "TagWithCallSite":
                // EF Core adds "File: {path}:{line}" at compile time; reproduce it from the constant arguments.
                if (argCount > 2 && node.Arguments[1] is ConstantExpression { Value: string path } && node.Arguments[2] is ConstantExpression { Value: int line })
                {
                    _tags.Add($"File: {path}:{line}");
                }

                break;
            case "Include":
                RecordInclude(node, parentPath: null);
                break;
            case "ThenInclude":
                RecordInclude(node, parentPath: FindIncludePath(node.Arguments[0]));
                break;
        }

        if (s_limitOperators.Contains(name))
        {
            _hasLimit = true;
        }

        if (s_predicateOperators.Contains(name) && argCount > 1 && name != "Where")
        {
            _hasFilter = true;
            _predicateLambdas++;
        }

        if (s_bulkOperators.Contains(name))
        {
            _isBulk = true;
        }

        _operators.Push(name);
        try
        {
            for (var i = 1; i < node.Arguments.Count; i++)
            {
                Visit(node.Arguments[i]);
            }
        }
        finally
        {
            _operators.Pop();
        }

        return node;
    }

    private string CurrentOperator => _operators.Count > 0 ? _operators.Peek() : string.Empty;

    private void RecordInclude(MethodCallExpression node, string? parentPath)
    {
        if (node.Arguments.Count < 2)
        {
            return;
        }

        var arg = node.Arguments[1];
        if (arg is ConstantExpression { Value: string path })
        {
            RecordStringInclude(path);
            return;
        }

        if (arg is UnaryExpression { Operand: LambdaExpression lambda } || arg is LambdaExpression)
        {
            lambda = arg is UnaryExpression u ? (LambdaExpression)u.Operand : (LambdaExpression)arg;
            var memberName = LastMemberName(lambda.Body);
            if (memberName is null)
            {
                return;
            }

            var fullPath = parentPath is null ? memberName : parentPath + "." + memberName;
            var isCollection = typeof(IEnumerable).IsAssignableFrom(lambda.ReturnType) && lambda.ReturnType != typeof(string);
            if (isCollection)
            {
                _includePaths.Add(fullPath);
            }

            if (lambda.Parameters.Count > 0 && _model?.FindEntityType(lambda.Parameters[0].Type) is { } owner)
            {
                var target = owner.FindNavigation(memberName)?.TargetEntityType ?? owner.FindSkipNavigation(memberName)?.TargetEntityType;
                if (target is not null)
                {
                    _includedTypes.Add(target.ClrType.FullName ?? target.Name);
                }
            }

            _includePathByNode[node] = fullPath;
        }
    }

    private readonly Dictionary<Expression, string> _includePathByNode = new(ReferenceEqualityComparer.Instance);

    private string? FindIncludePath(Expression source)
        => _includePathByNode.TryGetValue(source, out var p) ? p : null;

    private void RecordStringInclude(string path)
    {
        if (_root is null)
        {
            return;
        }

        IEntityType? current = _root;
        var soFar = new List<string>();
        foreach (var segment in path.Split('.'))
        {
            if (current is null)
            {
                break;
            }

            soFar.Add(segment);
            var nav = current.FindNavigation(segment);
            if (nav is not null)
            {
                if (nav.IsCollection)
                {
                    _includePaths.Add(string.Join('.', soFar));
                }

                current = nav.TargetEntityType;
                _includedTypes.Add(current.ClrType.FullName ?? current.Name);
                continue;
            }

            var skip = current.FindSkipNavigation(segment);
            if (skip is not null)
            {
                _includePaths.Add(string.Join('.', soFar));
                current = skip.TargetEntityType;
                _includedTypes.Add(current.ClrType.FullName ?? current.Name);
                continue;
            }

            break;
        }
    }

    private static string? LastMemberName(Expression body)
    {
        while (true)
        {
            switch (body)
            {
                case MemberExpression m:
                    return m.Member.Name;
                case UnaryExpression u:
                    body = u.Operand;
                    continue;
                case MethodCallExpression { Method.Name: "Where" or "OrderBy" or "OrderByDescending" or "Take" or "Skip" } filtered when filtered.Arguments.Count > 0:
                    body = filtered.Arguments[0];
                    continue;
                default:
                    return null;
            }
        }
    }

    private void TryRecordKeyFilter(Expression memberSide, Expression valueSide, bool solePredicate)
    {
        if (StripConvert(memberSide) is not MemberExpression { Expression: ParameterExpression param } member
            || !_entityLambdaParameters.Contains(param)
            || !IsQueryParameter(StripConvert(valueSide)))
        {
            return;
        }

        var entityType = _model?.FindEntityType(param.Type);
        var property = entityType?.FindProperty(member.Member.Name);
        if (entityType is null || property is null)
        {
            return;
        }

        if (property.IsForeignKey())
        {
            foreach (var fk in property.GetContainingForeignKeys())
            {
                _keyFilters.Add(new KeyFilter(
                    entityType.ClrType.Name,
                    property.Name,
                    IsPrimaryKey: false,
                    fk.PrincipalEntityType.ClrType.Name,
                    fk.PrincipalToDependent?.Name,
                    fk.DependentToPrincipal?.Name,
                    solePredicate));
            }
        }
        else if (property.IsPrimaryKey())
        {
            var referencing = entityType.GetReferencingForeignKeys().ToList();
            if (referencing.Count == 0)
            {
                _keyFilters.Add(new KeyFilter(entityType.ClrType.Name, property.Name, IsPrimaryKey: true, null, null, null, solePredicate));
            }

            foreach (var fk in referencing)
            {
                _keyFilters.Add(new KeyFilter(
                    entityType.ClrType.Name,
                    property.Name,
                    IsPrimaryKey: true,
                    fk.DeclaringEntityType.ClrType.Name,
                    fk.DependentToPrincipal?.Name,
                    fk.PrincipalToDependent?.Name,
                    solePredicate));
            }
        }
    }

    private static Expression StripConvert(Expression e)
    {
        while (e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u)
        {
            e = u.Operand;
        }

        return e;
    }

    /// <summary>EF Core 8 represents extracted parameters as <see cref="ParameterExpression"/> named <c>__x_0</c>; EF Core 10 uses <c>QueryParameterExpression</c>.</summary>
    private static bool IsQueryParameter(Expression e)
        => e is ParameterExpression { Name: { } n } && n.StartsWith("__", StringComparison.Ordinal)
           || e.GetType().Name == "QueryParameterExpression";

    private void InspectCallInsideLambda(MethodCallExpression node)
    {
        var method = node.Method;
        var declaring = method.DeclaringType;

        if (method.Name == "Contains")
        {
            var collection = declaring == typeof(Enumerable) || declaring == typeof(Queryable)
                ? (node.Arguments.Count > 0 ? node.Arguments[0] : null)
                : node.Object;
            if (collection is not null && IsQueryParameter(StripConvert(collection)))
            {
                _hasParameterCollectionContains = true;
            }
        }

        if (IsTranslatableOrigin(method))
        {
            return;
        }

        var parameters = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name));
        var owner = declaring?.Name ?? "?";
        _clientCalls.Add(new ClientEvaluatedCall($"{owner}.{method.Name}({parameters})", declaring?.FullName ?? owner, CurrentOperator));
    }

    private bool IsTranslatableOrigin(MethodInfo method)
    {
        if (_dbFunctions.Contains(method) || (method.IsGenericMethod && _dbFunctions.Contains(method.GetGenericMethodDefinition())))
        {
            return true;
        }

        var ns = method.DeclaringType?.Namespace ?? string.Empty;
        return ns.StartsWith("System", StringComparison.Ordinal)
               || ns.StartsWith("Microsoft.", StringComparison.Ordinal)
               || ns.StartsWith("Npgsql", StringComparison.Ordinal)
               || ns.StartsWith("Pomelo", StringComparison.Ordinal)
               || ns.StartsWith("Oracle", StringComparison.Ordinal)
               || ns.StartsWith("NetTopologySuite", StringComparison.Ordinal);
    }
}
