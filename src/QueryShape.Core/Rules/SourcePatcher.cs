using System.Text;
using System.Text.RegularExpressions;

namespace QueryShape.Rules;

/// <summary>Produces real unified diffs against a source file when the call site is known and the file is readable. Best-effort; returns <c>null</c> when unsure.</summary>
internal static partial class SourcePatcher
{
    /// <summary>
    /// Inserts <paramref name="insertion"/> (e.g. <c>.Include(c =&gt; c.Orders)</c>) before the terminal operator (<c>ToListAsync</c>, <c>ToList</c>, <c>First...</c>)
    /// found at the call-site line or within the next few lines.
    /// </summary>
    public static string? TryInsertBeforeTerminalOperator(CallSite site, string insertion)
    {
        if (site.FilePath is null || site.Line <= 0)
        {
            return null;
        }

        string[] lines;
        try
        {
            if (!File.Exists(site.FilePath))
            {
                return null;
            }

            lines = File.ReadAllLines(site.FilePath);
        }
        catch
        {
            return null;
        }

        for (var i = site.Line - 1; i < Math.Min(lines.Length, site.Line + 5); i++)
        {
            if (i < 0)
            {
                continue;
            }

            var m = Terminal().Match(lines[i]);
            if (!m.Success)
            {
                continue;
            }

            var line = lines[i];
            if (line.Contains(insertion, StringComparison.Ordinal))
            {
                return null;
            }

            var patched = line[..m.Index] + insertion + line[m.Index..];
            return UnifiedDiff(site.FilePath, lines, i, patched);
        }

        return null;
    }

    /// <summary>Builds a unified diff replacing one line, with three lines of context.</summary>
    internal static string UnifiedDiff(string path, string[] lines, int index, string replacement)
    {
        var start = Math.Max(0, index - 3);
        var end = Math.Min(lines.Length - 1, index + 3);
        var oldCount = end - start + 1;
        var sb = new StringBuilder();
        var name = RepositoryRelativePath(path);
        sb.Append("--- a/").Append(name).Append('\n');
        sb.Append("+++ b/").Append(name).Append('\n');
        sb.Append("@@ -").Append(start + 1).Append(',').Append(oldCount).Append(" +").Append(start + 1).Append(',').Append(oldCount).Append(" @@\n");
        for (var i = start; i <= end; i++)
        {
            if (i == index)
            {
                sb.Append('-').Append(lines[i]).Append('\n');
                sb.Append('+').Append(replacement).Append('\n');
            }
            else
            {
                sb.Append(' ').Append(lines[i]).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>Path relative to the enclosing git repository (so <c>git apply</c> works from the root), else the full path without a leading slash.</summary>
    internal static string RepositoryRelativePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
                {
                    return Path.GetRelativePath(dir, full).Replace('\\', '/');
                }

                dir = Path.GetDirectoryName(dir);
            }
        }
        catch
        {
            // Fall through to the plain path.
        }

        return path.Replace('\\', '/').TrimStart('/');
    }

    [GeneratedRegex(@"\.(ToListAsync|ToList|ToArrayAsync|ToArray|ToDictionaryAsync|ToDictionary|FirstOrDefaultAsync|FirstOrDefault|FirstAsync|First|SingleOrDefaultAsync|SingleOrDefault|SingleAsync|Single|AsAsyncEnumerable|AsEnumerable|ToHashSetAsync|ToHashSet)\s*\(")]
    private static partial Regex Terminal();
}
