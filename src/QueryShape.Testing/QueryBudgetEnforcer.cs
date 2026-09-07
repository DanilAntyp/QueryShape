using System.Collections.Concurrent;
using System.Reflection;

namespace QueryShape.Testing;

/// <summary>Shared logic for framework adapters that wrap a test method in a scope and assert a <see cref="QueryBudget"/> afterwards.</summary>
public static class QueryBudgetEnforcer
{
    private static readonly AsyncLocal<QueryShapeScope?> s_current = new();
    private static readonly ConcurrentDictionary<MethodInfo, QueryShapeScope> s_byMethod = new();

    /// <summary>Begins a scope for <paramref name="method"/>. Call from the framework's before-test hook.</summary>
    public static QueryShapeScope Begin(MethodInfo method, QueryShapeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        var scope = QueryShapeScope.Begin($"{method.DeclaringType?.Name}.{method.Name}", options ?? QueryShapeTest.DefaultOptions);
        s_current.Value = scope;
        s_byMethod[method] = scope;
        return scope;
    }

    /// <summary>Closes the scope begun for <paramref name="method"/> without checking the budget (the test already failed).</summary>
    public static void Discard(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var scope = s_current.Value;
        s_byMethod.TryRemove(method, out var registered);
        scope ??= registered;
        s_current.Value = null;
        scope?.Dispose();
    }

    /// <summary>Ends the scope begun for <paramref name="method"/> and throws <see cref="QueryBudgetExceededException"/> when the budget is exceeded.</summary>
    public static void End(MethodInfo method, QueryBudget budget)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(budget);

        var scope = s_current.Value;
        s_byMethod.TryRemove(method, out var registered);
        scope ??= registered;

        s_current.Value = null;
        if (scope is null)
        {
            return;
        }

        try
        {
            scope.AssertBudget(budget, scope.Name);
        }
        finally
        {
            scope.Dispose();
        }
    }
}
