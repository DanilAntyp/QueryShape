using System.Text.RegularExpressions;

namespace QueryShape.Cli.Llm;

/// <summary>Extracts the method around a call site: the smallest enclosing member, never the whole file.</summary>
internal static partial class SourceExcerpt
{
    public sealed record Excerpt(string Path, int StartLine, int EndLine, string Text);

    public static Excerpt? Extract(string? path, int line, int fallbackRadius = 25)
    {
        if (path is null || line <= 0 || !File.Exists(path))
        {
            return null;
        }

        var lines = File.ReadAllLines(path);
        var idx = Math.Min(line - 1, lines.Length - 1);

        // Walk up to a member signature line, then brace-match forward.
        for (var start = idx; start >= 0 && idx - start < 400; start--)
        {
            if (!LooksLikeMemberSignature(lines[start]))
            {
                continue;
            }

            var end = FindBlockEnd(lines, start);
            if (end >= idx)
            {
                return new Excerpt(path, start + 1, end + 1, string.Join('\n', lines[start..(end + 1)]));
            }
        }

        var from = Math.Max(0, idx - fallbackRadius);
        var to = Math.Min(lines.Length - 1, idx + fallbackRadius);
        return new Excerpt(path, from + 1, to + 1, string.Join('\n', lines[from..(to + 1)]));
    }

    private static bool LooksLikeMemberSignature(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal) || ControlKeyword().IsMatch(t))
        {
            return false;
        }

        return MemberSignature().IsMatch(t);
    }

    private static int FindBlockEnd(string[] lines, int start)
    {
        var depth = 0;
        var seenOpen = false;
        for (var i = start; i < lines.Length; i++)
        {
            foreach (var ch in lines[i])
            {
                if (ch == '{')
                {
                    depth++;
                    seenOpen = true;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (seenOpen && depth == 0)
                    {
                        return i;
                    }
                }
            }

            if (!seenOpen && lines[i].TrimEnd().EndsWith(';'))
            {
                return i; // expression-bodied member
            }
        }

        return lines.Length - 1;
    }

    [GeneratedRegex(@"^(if|else|for|foreach|while|do|switch|case|using|return|try|catch|finally|lock|throw|await|var|new)\b")]
    private static partial Regex ControlKeyword();

    // "public async Task<Foo> Bar(", "static void Baz(", "app.MapGet(" (endpoint lambdas), "=> {"
    [GeneratedRegex(@"^(?:(?:public|private|protected|internal|static|async|override|virtual|sealed|partial|extern|unsafe|new)\s+)*[\w<>\[\],\.\?\s]+\s+\w+\s*(?:<[^>]+>)?\s*\(.*\)\s*(?:where\s+.*)?(?:\{|=>)?\s*$|^\w+(?:\.\w+)*\s*\(\s*""[^""]*""\s*,\s*(?:async\s*)?\(.*=>\s*\{?\s*$")]
    private static partial Regex MemberSignature();
}
