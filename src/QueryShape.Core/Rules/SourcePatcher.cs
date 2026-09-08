using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace QueryShape.Rules;

/// <summary>Produces real unified diffs against source files when the call site is known and the file is readable. Best-effort; returns <c>null</c> when unsure.</summary>
internal static partial class SourcePatcher
{
    /// <summary>One line-level change: replace line <see cref="Index"/> (0-based) with <see cref="Replacement"/>, or delete it when <c>null</c>.</summary>
    internal sealed record LineEdit(string Path, int Index, string? Replacement);

    /// <summary>
    /// Inserts <paramref name="insertion"/> (e.g. <c>.Include(c =&gt; c.Orders)</c>) before the terminal operator (<c>ToListAsync</c>, <c>ToList</c>, <c>First...</c>)
    /// found at the call-site line or within the next few lines. Returns the edit, or <c>null</c>.
    /// </summary>
    public static LineEdit? TryInsertBeforeTerminalOperatorEdit(CallSite site, string insertion)
    {
        var lines = ReadLines(site);
        if (lines is null)
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

            return new LineEdit(site.FilePath!, i, line[..m.Index] + insertion + line[m.Index..]);
        }

        return null;
    }

    /// <summary>Inserts <paramref name="insertion"/> before the first row-limiting operator (<c>Take</c>, <c>Skip</c>, <c>First...</c>, <c>Last...</c>) at or shortly after the call-site line.</summary>
    public static string? TryInsertBeforeRowLimitingOperator(CallSite site, string insertion)
    {
        var lines = ReadLines(site);
        if (lines is null)
        {
            return null;
        }

        for (var i = site.Line - 1; i < Math.Min(lines.Length, site.Line + 5); i++)
        {
            if (i < 0)
            {
                continue;
            }

            var m = RowLimiting().Match(lines[i]);
            if (!m.Success || lines[i].Contains(".OrderBy", StringComparison.Ordinal) || lines[i].Contains(insertion, StringComparison.Ordinal))
            {
                continue;
            }

            var line = lines[i];
            return BuildDiff([new LineEdit(site.FilePath!, i, line[..m.Index] + insertion + line[m.Index..])]);
        }

        return null;
    }

    /// <summary>Convenience: the single-edit diff for <see cref="TryInsertBeforeTerminalOperatorEdit"/>.</summary>
    public static string? TryInsertBeforeTerminalOperator(CallSite site, string insertion)
    {
        var edit = TryInsertBeforeTerminalOperatorEdit(site, insertion);
        return edit is null ? null : BuildDiff([edit]);
    }

    /// <summary>
    /// Rewrites the per-item query inside a loop to read the navigation that an <c>Include</c> on the parent query now fills:
    /// <c>var orders = await db.Orders.Where(o =&gt; o.CustomerId == customer.Id).ToListAsync();</c> becomes <c>var orders = customer.Orders;</c>.
    /// Only the unambiguous shapes are handled; anything else returns <c>null</c> so the caller can report a partial fix.
    /// </summary>
    /// <param name="site">Call site of the repeated query.</param>
    /// <param name="filterProperty">The key/foreign-key property compared inside the lambda (e.g. <c>CustomerId</c>).</param>
    /// <param name="navigation">Navigation on the parent that the Include loads (e.g. <c>Orders</c>).</param>
    /// <param name="navigationIsCollection"><c>true</c> for a collection navigation, <c>false</c> for a reference.</param>
    /// <param name="parentVariable">The loop variable found in the source, when the rewrite succeeded.</param>
    public static LineEdit? TryRewriteLoopQuery(CallSite site, string filterProperty, string navigation, bool navigationIsCollection, out string? parentVariable)
    {
        parentVariable = null;
        var lines = ReadLines(site);
        if (lines is null || site.Line - 1 >= lines.Length)
        {
            return null;
        }

        var index = site.Line - 1;
        var line = lines[index];
        var statement = Statement().Match(line);
        if (!statement.Success)
        {
            return null;
        }

        var expr = statement.Groups["expr"].Value;
        var lambda = LambdaParameter().Match(expr);
        if (!lambda.Success)
        {
            return null;
        }

        var param = lambda.Groups["param"].Value;
        string? parent = null;
        foreach (Match cmp in Comparison().Matches(expr))
        {
            var (lo, lp, ro, rp) = (cmp.Groups["lo"].Value, cmp.Groups["lp"].Value, cmp.Groups["ro"].Value, cmp.Groups["rp"].Value);
            if (lo == param && lp == filterProperty && ro != param)
            {
                parent = ro;
                break;
            }

            if (ro == param && rp == filterProperty && lo != param)
            {
                parent = lo;
                break;
            }
        }

        if (parent is null || parent is "this" or "db" or "context")
        {
            return null;
        }

        var terminal = TerminalAtEnd().Match(expr);
        if (!terminal.Success)
        {
            return null;
        }

        var lhs = statement.Groups["lhs"].Value.Trim();
        var indent = statement.Groups["indent"].Value;
        var op = terminal.Groups["op"].Value;
        var navigationRead = parent + "." + navigation;
        parentVariable = parent;

        // The loop assigns the navigation itself: the Include already did that, drop the line.
        if (lhs == navigationRead)
        {
            return new LineEdit(site.FilePath!, index, null);
        }

        if (!lhs.StartsWith("var ", StringComparison.Ordinal))
        {
            return null; // an explicitly typed target might not accept the navigation's type
        }

        string? replacement = op switch
        {
            "ToList" or "ToListAsync" or "ToArray" or "ToArrayAsync" when navigationIsCollection => $"{indent}{lhs} = {navigationRead};",
            "Count" or "CountAsync" when navigationIsCollection => $"{indent}{lhs} = {navigationRead}.Count;",
            "Any" or "AnyAsync" when navigationIsCollection => $"{indent}{lhs} = {navigationRead}.Count > 0;",
            "First" or "FirstAsync" or "FirstOrDefault" or "FirstOrDefaultAsync" or "Single" or "SingleAsync" or "SingleOrDefault" or "SingleOrDefaultAsync" when !navigationIsCollection
                => $"{indent}{lhs} = {navigationRead};",
            _ => null,
        };

        return replacement is null ? null : new LineEdit(site.FilePath!, index, replacement);
    }

    /// <summary>Builds one unified diff (several file sections if needed) from line edits, with three lines of context and merged hunks.</summary>
    internal static string BuildDiff(IReadOnlyList<LineEdit> edits)
    {
        var sb = new StringBuilder();
        foreach (var file in edits.GroupBy(e => e.Path, StringComparer.Ordinal))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file.Key);
            }
            catch
            {
                continue;
            }

            var byIndex = file.GroupBy(e => e.Index).ToDictionary(g => g.Key, g => g.Last());
            var ordered = byIndex.Keys.Where(i => i >= 0 && i < lines.Length).OrderBy(i => i).ToList();
            if (ordered.Count == 0)
            {
                continue;
            }

            // Merge edits whose context windows touch into one hunk.
            var hunks = new List<(int Start, int End)>();
            foreach (var i in ordered)
            {
                var start = Math.Max(0, i - 3);
                var end = Math.Min(lines.Length - 1, i + 3);
                if (hunks.Count > 0 && start <= hunks[^1].End + 1)
                {
                    hunks[^1] = (hunks[^1].Start, Math.Max(hunks[^1].End, end));
                }
                else
                {
                    hunks.Add((start, end));
                }
            }

            var name = RepositoryRelativePath(file.Key);
            sb.Append("--- a/").Append(name).Append('\n');
            sb.Append("+++ b/").Append(name).Append('\n');

            var delta = 0;
            foreach (var (start, end) in hunks)
            {
                var oldCount = end - start + 1;
                var removed = 0;
                var added = 0;
                var body = new StringBuilder();
                for (var i = start; i <= end; i++)
                {
                    if (byIndex.TryGetValue(i, out var edit))
                    {
                        body.Append('-').Append(lines[i]).Append('\n');
                        removed++;
                        if (edit.Replacement is not null)
                        {
                            body.Append('+').Append(edit.Replacement).Append('\n');
                            added++;
                        }
                    }
                    else
                    {
                        body.Append(' ').Append(lines[i]).Append('\n');
                    }
                }

                var newCount = oldCount - removed + added;
                sb.Append("@@ -").Append((start + 1).ToString(CultureInfo.InvariantCulture)).Append(',').Append(oldCount.ToString(CultureInfo.InvariantCulture))
                  .Append(" +").Append((start + 1 + delta).ToString(CultureInfo.InvariantCulture)).Append(',').Append(newCount.ToString(CultureInfo.InvariantCulture)).Append(" @@\n");
                sb.Append(body);
                delta += newCount - oldCount;
            }
        }

        return sb.Length == 0 ? string.Empty : sb.ToString();
    }

    /// <summary>Builds a unified diff replacing one line, with three lines of context (kept for callers with in-memory lines).</summary>
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

    private static string[]? ReadLines(CallSite site)
    {
        if (site.FilePath is null || site.Line <= 0)
        {
            return null;
        }

        try
        {
            return File.Exists(site.FilePath) ? File.ReadAllLines(site.FilePath) : null;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"\.(ToListAsync|ToList|ToArrayAsync|ToArray|ToDictionaryAsync|ToDictionary|FirstOrDefaultAsync|FirstOrDefault|FirstAsync|First|SingleOrDefaultAsync|SingleOrDefault|SingleAsync|Single|AsAsyncEnumerable|AsEnumerable|ToHashSetAsync|ToHashSet|CountAsync|Count|AnyAsync|Any)\s*\(")]
    private static partial Regex Terminal();

    [GeneratedRegex(@"\.(Take|Skip|TakeLast|SkipLast|FirstOrDefaultAsync|FirstOrDefault|FirstAsync|First|LastOrDefaultAsync|LastOrDefault|LastAsync|Last|ElementAtAsync|ElementAt|ElementAtOrDefaultAsync|ElementAtOrDefault)\s*\(")]
    private static partial Regex RowLimiting();

    // "    var orders = await db.Orders.Where(...).ToListAsync();"  /  "    customer.Orders = await ...;"
    [GeneratedRegex(@"^(?<indent>\s*)(?<lhs>(?:var\s+)?[\w.]+)\s*=\s*(?<await>await\s+)?(?<expr>[^;]+?)\s*;\s*$")]
    private static partial Regex Statement();

    [GeneratedRegex(@"\b(?<param>\w+)\s*=>")]
    private static partial Regex LambdaParameter();

    [GeneratedRegex(@"\b(?<lo>\w+)\.(?<lp>\w+)\s*==\s*(?<ro>\w+)\.(?<rp>\w+)\b")]
    private static partial Regex Comparison();

    [GeneratedRegex(@"\.(?<op>ToListAsync|ToList|ToArrayAsync|ToArray|FirstOrDefaultAsync|FirstOrDefault|FirstAsync|First|SingleOrDefaultAsync|SingleOrDefault|SingleAsync|Single|CountAsync|Count|AnyAsync|Any)\s*\([^()]*\)\s*$")]
    private static partial Regex TerminalAtEnd();
}
