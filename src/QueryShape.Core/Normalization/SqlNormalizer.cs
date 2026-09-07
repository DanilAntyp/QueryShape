using System.Text;
using System.Text.RegularExpressions;

namespace QueryShape.Normalization;

/// <summary>
/// Turns provider SQL into a deterministic <em>shape</em>: comments removed, whitespace collapsed,
/// EF Core's generated table aliases (<c>[c]</c>, <c>"o0"</c>, <c>t</c>) renamed positionally to <c>t0</c>, <c>t1</c>…
/// Parameter names are kept; parameter values never appear in SQL text so nothing has to be stripped.
/// Two commands with the same shape are "the same query with different arguments".
/// </summary>
public static partial class SqlNormalizer
{
    /// <summary>Normalizes <paramref name="sql"/> and returns the shape plus any leading <c>TagWith</c> tags.</summary>
    public static NormalizedSql Normalize(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var tags = ExtractTags(sql, out var body);
        body = BlockComment().Replace(body, " ");
        body = LineComment().Replace(body, " ");
        body = Whitespace().Replace(body, " ").Trim();
        body = CanonicalizeAliases(body);
        return new NormalizedSql(body, tags);
    }

    /// <summary>Shape only, without tags.</summary>
    public static string Shape(string sql) => Normalize(sql).Shape;

    private static List<string> ExtractTags(string sql, out string rest)
    {
        // EF Core writes tags as leading "-- text" lines (one per tag line) followed by a blank line.
        var tags = new List<string>();
        var lines = sql.Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith("--", StringComparison.Ordinal))
            {
                var text = line.Length > 3 ? line[3..] : string.Empty;
                if (text.Length > 0)
                {
                    tags.Add(text);
                }

                i++;
                continue;
            }

            if (line.Length == 0 && tags.Count > 0)
            {
                i++;
                continue;
            }

            break;
        }

        rest = string.Join('\n', lines, i, lines.Length - i);
        return tags;
    }

    private static string CanonicalizeAliases(string sql)
    {
        // Aliases introduced by "FROM x AS a", "JOIN x AS a", ") AS a" (derived tables / APPLY).
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in AliasIntroduction().Matches(sql))
        {
            var raw = m.Groups["alias"].Value;
            var bare = Unquote(raw);
            if (!LooksGenerated(bare) || map.ContainsKey(bare))
            {
                continue;
            }

            map[bare] = "t" + map.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (map.Count == 0)
        {
            return sql;
        }

        return AliasReference().Replace(sql, m =>
        {
            var bare = Unquote(m.Groups["id"].Value);
            if (!map.TryGetValue(bare, out var canonical))
            {
                return m.Value;
            }

            var id = m.Groups["id"].Value;
            var quoted = id[0] switch
            {
                '[' => "[" + canonical + "]",
                '"' => "\"" + canonical + "\"",
                '`' => "`" + canonical + "`",
                _ => canonical,
            };
            return m.Groups["pre"].Value + quoted;
        });
    }

    private static bool LooksGenerated(string alias)
        // EF Core generates short lowercase aliases: c, o, o0, t, t1, s, e...
        => alias.Length is >= 1 and <= 4 && char.IsAsciiLetterLower(alias[0]) && alias.Skip(1).All(char.IsAsciiLetterOrDigit) && alias.Skip(1).Where(char.IsLetter).All(char.IsAsciiLetterLower);

    private static string Unquote(string id)
        => id.Length >= 2 && (id[0] == '[' && id[^1] == ']' || id[0] == '"' && id[^1] == '"' || id[0] == '`' && id[^1] == '`')
            ? id[1..^1]
            : id;

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"--[^\r\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // "FROM [Customers] AS [c]", "JOIN "Orders" AS o", ") AS [t0]", "APPLY (...) AS [t]"
    [GeneratedRegex(@"(?:\b(?:FROM|JOIN)\s+(?:\[[^\]]+\]|""[^""]+""|`[^`]+`|[\w.]+)|\))\s+AS\s+(?<alias>\[[^\]]+\]|""[^""]+""|`[^`]+`|\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex AliasIntroduction();

    // An identifier that is quoted, or bare and preceded by AS, or bare and followed by a dot.
    [GeneratedRegex(@"(?:(?<pre>\bAS\s+)(?<id>\w+)\b(?!\.)|(?<id>\[[^\]]+\]|""[^""]+""|`[^`]+`)|\b(?<id>\w+)(?=\.))", RegexOptions.IgnoreCase)]
    private static partial Regex AliasReference();
}

/// <summary>Result of <see cref="SqlNormalizer.Normalize"/>.</summary>
/// <param name="Shape">The normalized SQL.</param>
/// <param name="Tags">Tags extracted from leading comments.</param>
public sealed record NormalizedSql(string Shape, IReadOnlyList<string> Tags)
{
    /// <summary>Fingerprint of <see cref="Shape"/>.</summary>
    public string Fingerprint { get; } = Normalization.Fingerprint.Compute(Shape);
}
