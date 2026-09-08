using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QueryShape.Testing;

/// <summary>How to compare a result collection. Nested arrays retain their order.</summary>
public enum ResultOrder
{
    Preserve,
    Ignore,
}

/// <summary>Explicit observations for checking result and database-state preservation across patches.</summary>
public static class QueryBehavior
{
    /// <summary>Compares local values without writing reports. Raw values require explicit opt-in.</summary>
    public static IReadOnlyList<BehaviorDifference> Compare<T>(T before, T after, BehaviorDiffOptions? options = null) =>
        BehaviorDiffer.Compare(before, after, options ?? new());
    /// <summary>Records only a digest, never the supplied values. Call before disposing the scope. Use stable, non-sensitive names.</summary>
    public static void Observe<T>(this QueryShapeScope scope, string name, T value, ResultOrder order = ResultOrder.Preserve)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (scope.IsCompleted) throw new InvalidOperationException("Observe behavior before the scope is disposed.");
        var key = "behavior.v1." + name;
        if (scope.Annotations.ContainsKey(key)) throw new InvalidOperationException("Behavior observation names must be unique within a scope: " + name);
        scope.Annotate(key, Digest(value, order));
    }

    /// <summary>Hashes canonical JSON: sorted object properties, preserved arrays by default, and duplicate-preserving root-array sorting when requested.</summary>
    public static string Digest<T>(T value, ResultOrder order = ResultOrder.Preserve)
    {
        if (!Enum.IsDefined(order)) throw new ArgumentOutOfRangeException(nameof(order));
        var element = JsonSerializer.SerializeToElement(value);
        var canonical = Canonical(element, order == ResultOrder.Ignore);
        return order + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string Canonical(JsonElement value, bool sortArray = false) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", sortArray
            ? value.EnumerateArray().Select(v => Canonical(v)).OrderBy(v => v, StringComparer.Ordinal)
            : value.EnumerateArray().Select(v => Canonical(v))) + "]",
        _ => value.GetRawText(),
    };
}
