using System.Text;
using System.Text.RegularExpressions;

namespace QueryShape.Normalization;

/// <summary>
/// Turns provider SQL into a deterministic <em>shape</em>: comments removed, whitespace collapsed,
/// EF Core's generated table aliases (<c>[c]</c>, <c>"o0"</c>, <c>t</c>) renamed positionally to <c>t0</c>, <c>t1</c>…,
/// and parameter names (<c>@__customerId_0</c> in EF Core 8, <c>@customerId</c> in EF Core 10) renamed positionally to <c>@p0</c>, <c>@p1</c>…
/// String literals are recognized first and never touched by those passes (a <c>--</c>, an <c>@</c> or two spaces inside a literal stay as they are).
/// EF Core sends values as parameters, so a LINQ query's text carries only the constants written in the source. Raw SQL can carry
/// anything (a value concatenated into the text), so raw shapes additionally mask string and numeric literals as <c>?</c>.
/// Two commands with the same shape are "the same query with different arguments". See ADR-0006.
/// </summary>
public static partial class SqlNormalizer
{
    /// <summary>Stands in for a string literal while the other passes run; restored (or masked) at the end.</summary>
    private const char LiteralSentinel = '\uE000';

    /// <summary>Normalizes <paramref name="sql"/> and returns the shape plus any leading <c>TagWith</c> tags.</summary>
    public static NormalizedSql Normalize(string sql) => Normalize(sql, maskLiterals: false);

    /// <summary>Normalizes <paramref name="sql"/>; with <paramref name="maskLiterals"/>, string and numeric literals become <c>?</c> (used for raw SQL, whose text may embed values).</summary>
    public static NormalizedSql Normalize(string sql, bool maskLiterals)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var tags = ExtractTags(sql, out var body);
        var literals = new List<string>();
        var shaped = Tokenize(body, literals);
        shaped = Whitespace().Replace(shaped, " ").Trim();
        shaped = CanonicalizeAliases(shaped);
        shaped = CanonicalizeParameters(shaped);
        if (maskLiterals)
        {
            shaped = NumericLiteral().Replace(shaped, "?");
        }

        return new NormalizedSql(RestoreLiterals(shaped, literals, maskLiterals), tags);
    }

    /// <summary>Shape only, without tags.</summary>
    public static string Shape(string sql) => Normalize(sql).Shape;

    /// <summary>Replaces string and numeric literals with <c>?</c> so texts that differ only in values collapse to one shape. Identifiers, aliases and parameter names are kept.</summary>
    public static string MaskLiterals(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var s = StringLiteral().Replace(sql, "?");
        return NumericLiteral().Replace(s, "?");
    }

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

    /// <summary>
    /// One pass over the SQL: string literals (with <c>''</c> escapes and an optional <c>N</c> prefix) become a sentinel and are collected,
    /// quoted identifiers (<c>"x"</c>, <c>[x]</c>, <c>`x`</c>) are copied verbatim, and line/block comments become a space.
    /// A quote inside a comment or a bracketed identifier therefore never opens a literal, and a <c>--</c> inside a literal never opens a comment.
    /// </summary>
    private static string Tokenize(string body, List<string> literals)
    {
        var sb = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var ch = body[i];
            switch (ch)
            {
                case '\'':
                {
                    var end = ScanStringLiteral(body, i);
                    var start = i;
                    // N'...' (SQL Server unicode literal): the prefix belongs to the literal.
                    if (sb.Length > 0 && (sb[^1] == 'N' || sb[^1] == 'n') && (sb.Length == 1 || !IsWordChar(sb[^2])))
                    {
                        sb.Length--;
                        start--;
                    }

                    literals.Add(body[start..(end + 1)]);
                    sb.Append(LiteralSentinel);
                    i = end;
                    break;
                }

                case '"':
                case '[':
                case '`':
                {
                    var end = ScanQuotedIdentifier(body, i, ch == '[' ? ']' : ch);
                    sb.Append(body, i, end - i + 1);
                    i = end;
                    break;
                }

                case '-' when i + 1 < body.Length && body[i + 1] == '-':
                {
                    var end = body.IndexOfAny(['\r', '\n'], i);
                    sb.Append(' ');
                    i = (end < 0 ? body.Length : end) - 1;
                    break;
                }

                case '/' when i + 1 < body.Length && body[i + 1] == '*':
                {
                    var end = body.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    sb.Append(' ');
                    i = (end < 0 ? body.Length : end + 2) - 1;
                    break;
                }

                default:
                    sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }

    private static int ScanStringLiteral(string text, int openIndex)
    {
        for (var i = openIndex + 1; i < text.Length; i++)
        {
            if (text[i] != '\'')
            {
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '\'')
            {
                i++; // '' escape
                continue;
            }

            return i;
        }

        return text.Length - 1; // unterminated: the rest of the text is the literal
    }

    private static int ScanQuotedIdentifier(string text, int openIndex, char close)
    {
        for (var i = openIndex + 1; i < text.Length; i++)
        {
            if (text[i] != close)
            {
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == close)
            {
                i++; // doubled closing quote escape
                continue;
            }

            return i;
        }

        return text.Length - 1;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string RestoreLiterals(string shaped, List<string> literals, bool mask)
    {
        if (literals.Count == 0)
        {
            return shaped;
        }

        var sb = new StringBuilder(shaped.Length + 16);
        var next = 0;
        foreach (var ch in shaped)
        {
            if (ch == LiteralSentinel)
            {
                sb.Append(mask ? "?" : next < literals.Count ? literals[next] : "?");
                next++;
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static string CanonicalizeAliases(string sql)
    {
        // Aliases introduced by "FROM x AS a", "JOIN x AS a", ") AS a" (derived tables / APPLY); x may be schema-qualified ([dbo].[Customers], public."Customers").
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

    private static string CanonicalizeParameters(string sql)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        return Parameter().Replace(sql, m =>
        {
            var name = m.Groups["name"].Value;
            if (!map.TryGetValue(name, out var canonical))
            {
                canonical = "@p" + map.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                map[name] = canonical;
            }

            return canonical;
        });
    }

    private static bool LooksGenerated(string alias)
        // EF Core generates short lowercase aliases: c, o, o0, t, t1, s, e...
        => alias.Length is >= 1 and <= 4 && char.IsAsciiLetterLower(alias[0]) && alias.Skip(1).All(char.IsAsciiLetterOrDigit) && alias.Skip(1).Where(char.IsLetter).All(char.IsAsciiLetterLower);

    private static string Unquote(string id)
        => id.Length >= 2 && (id[0] == '[' && id[^1] == ']' || id[0] == '"' && id[^1] == '"' || id[0] == '`' && id[^1] == '`')
            ? id[1..^1]
            : id;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // "@name" parameters (SQL Server, SQLite, Npgsql, MySQL); "@@" server variables are left alone.
    [GeneratedRegex(@"(?<![@\w])@(?<name>\w+)")]
    private static partial Regex Parameter();

    // "FROM [Customers] AS [c]", "FROM [dbo].[Customers] AS [c]", "JOIN "Orders" AS o", ") AS [t0]", "APPLY (...) AS [t]"
    [GeneratedRegex(@"(?:\b(?:FROM|JOIN)\s+(?:(?:\[[^\]]+\]|""[^""]+""|`[^`]+`|\w+)\.)*(?:\[[^\]]+\]|""[^""]+""|`[^`]+`|\w+)|\))\s+AS\s+(?<alias>\[[^\]]+\]|""[^""]+""|`[^`]+`|\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex AliasIntroduction();

    // An identifier that is quoted, or bare and preceded by AS, or bare and followed by a dot.
    [GeneratedRegex(@"(?:(?<pre>\bAS\s+)(?<id>\w+)\b(?!\.)|(?<id>\[[^\]]+\]|""[^""]+""|`[^`]+`)|\b(?<id>\w+)(?=\.))", RegexOptions.IgnoreCase)]
    private static partial Regex AliasReference();

    // 'text' with '' escapes (also N'text').
    [GeneratedRegex(@"N?'(?:[^']|'')*'")]
    private static partial Regex StringLiteral();

    // Numbers that are not part of an identifier or a parameter name (@p0, t0, [c1]).
    [GeneratedRegex(@"(?<![\w@\]""`.])\b\d+(?:\.\d+)?\b(?![\w\]""`])")]
    private static partial Regex NumericLiteral();
}

/// <summary>Result of <see cref="SqlNormalizer.Normalize(string)"/>.</summary>
/// <param name="Shape">The normalized SQL.</param>
/// <param name="Tags">Tags extracted from leading comments.</param>
public sealed record NormalizedSql(string Shape, IReadOnlyList<string> Tags)
{
    /// <summary>Fingerprint of <see cref="Shape"/>.</summary>
    public string Fingerprint { get; } = Normalization.Fingerprint.Compute(Shape);
}
