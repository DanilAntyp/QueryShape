using System.Text.Json;

namespace QueryShape.Testing;

/// <summary>Explicit local comparison options. Values are hidden unless enabled; redacted paths hide their entire subtree.</summary>
public sealed class BehaviorDiffOptions
{
    public bool IncludeValues { get; init; }
    public Func<string, bool>? RedactPath { get; init; }
    public int MaxDifferences { get; init; } = 20;
}

public sealed record BehaviorDifference(string Path, string Before, string After);

internal static class BehaviorDiffer
{
    internal static IReadOnlyList<BehaviorDifference> Compare(object? before, object? after, BehaviorDiffOptions options, string root = "$")
    {
        if (options.MaxDifferences is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(options));
        var differences = new List<BehaviorDifference>();
        Visit(JsonSerializer.SerializeToElement(before), JsonSerializer.SerializeToElement(after), root);
        return differences;

        void Visit(JsonElement a, JsonElement b, string path)
        {
            if (differences.Count >= options.MaxDifferences || QueryBehavior.Digest(a) == QueryBehavior.Digest(b)) return;
            if (options.RedactPath?.Invoke(path) == true) { differences.Add(new(path, "[redacted]", "[redacted]")); return; }
            if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in a.EnumerateObject().Select(p => p.Name).Union(b.EnumerateObject().Select(p => p.Name)).Order(StringComparer.Ordinal))
                {
                    var child = path + "[" + JsonSerializer.Serialize(name) + "]";
                    if (a.TryGetProperty(name, out var av) && b.TryGetProperty(name, out var bv)) Visit(av, bv, child);
                    else Add(child, a.TryGetProperty(name, out av) ? av : null, b.TryGetProperty(name, out bv) ? bv : null);
                }
            }
            else if (a.ValueKind == JsonValueKind.Array && b.ValueKind == JsonValueKind.Array)
            {
                for (var i = 0; i < Math.Max(a.GetArrayLength(), b.GetArrayLength()) && differences.Count < options.MaxDifferences; i++)
                {
                    var child = path + "[" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
                    if (i < a.GetArrayLength() && i < b.GetArrayLength()) Visit(a[i], b[i], child);
                    else Add(child, i < a.GetArrayLength() ? a[i] : null, i < b.GetArrayLength() ? b[i] : null);
                }
            }
            else Add(path, a, b);
        }
        void Add(string path, JsonElement? a, JsonElement? b)
        {
            if (differences.Count >= options.MaxDifferences) return;
            var visible = options.IncludeValues && options.RedactPath?.Invoke(path) != true;
            differences.Add(new(path, visible ? a?.GetRawText() ?? "[missing]" : "[hidden]", visible ? b?.GetRawText() ?? "[missing]" : "[hidden]"));
        }
    }
}
