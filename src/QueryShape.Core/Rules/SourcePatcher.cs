using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace QueryShape.Rules;

/// <summary>Produces real unified diffs against source files when the call site is known and the file is readable. Best-effort; returns <c>null</c> when unsure.</summary>
internal static partial class SourcePatcher
{
    /// <summary>One line-level change: replace line <see cref="Index"/> (0-based) with <see cref="Replacement"/>, or delete it when <c>null</c>.</summary>
    internal sealed record LineEdit(string Path, int Index, string? Replacement);

    private static readonly HashSet<string> s_terminals = new(StringComparer.Ordinal)
    {
        "ToListAsync", "ToList", "ToArrayAsync", "ToArray", "ToDictionaryAsync", "ToDictionary", "ToHashSetAsync", "ToHashSet",
        "FirstOrDefaultAsync", "FirstOrDefault", "FirstAsync", "First", "SingleOrDefaultAsync", "SingleOrDefault", "SingleAsync", "Single",
        "AsAsyncEnumerable", "AsEnumerable", "CountAsync", "Count", "AnyAsync", "Any",
    };

    private static readonly HashSet<string> s_rowLimiting = new(StringComparer.Ordinal)
    {
        "Take", "Skip", "TakeLast", "SkipLast", "FirstOrDefaultAsync", "FirstOrDefault", "FirstAsync", "First",
        "LastOrDefaultAsync", "LastOrDefault", "LastAsync", "Last", "ElementAtAsync", "ElementAt", "ElementAtOrDefaultAsync", "ElementAtOrDefault",
    };

    /// <summary>Operators that do not change what a loop query returns, so a navigation read can replace the query even when they are present.</summary>
    private static readonly HashSet<string> s_neutralOperators = new(StringComparer.Ordinal)
    {
        "AsNoTracking", "AsNoTrackingWithIdentityResolution", "AsTracking", "TagWith", "TagWithCallSite",
    };

    private static readonly HashSet<string> s_collectionTerminals = new(StringComparer.Ordinal)
    {
        "ToList", "ToListAsync", "ToArray", "ToArrayAsync", "Count", "CountAsync", "Any", "AnyAsync",
    };

    private static readonly HashSet<string> s_referenceTerminals = new(StringComparer.Ordinal)
    {
        "First", "FirstAsync", "FirstOrDefault", "FirstOrDefaultAsync", "Single", "SingleAsync", "SingleOrDefault", "SingleOrDefaultAsync",
    };

    /// <summary>
    /// Inserts <paramref name="insertion"/> (e.g. <c>.Include(c =&gt; c.Orders)</c>) before the terminal operator (<c>ToListAsync</c>, <c>ToList</c>, <c>First...</c>)
    /// found at the call-site line or within the next few lines. Only a terminal at the statement level counts: one inside a lambda
    /// (<c>Where(c =&gt; c.Orders.Any())</c>) is skipped. Returns the edit, or <c>null</c>.
    /// </summary>
    public static LineEdit? TryInsertBeforeTerminalOperatorEdit(CallSite site, string insertion)
        => TryInsertBeforeCall(site, insertion, s_terminals, static _ => true);

    /// <summary>Inserts <paramref name="insertion"/> before the first row-limiting operator (<c>Take</c>, <c>Skip</c>, <c>First...</c>, <c>Last...</c>) at or shortly after the call-site line.</summary>
    public static string? TryInsertBeforeRowLimitingOperator(CallSite site, string insertion)
    {
        var edit = TryInsertBeforeCall(site, insertion, s_rowLimiting, static line => !line.Contains(".OrderBy", StringComparison.Ordinal));
        return edit is null ? null : BuildDiff([edit]);
    }

    /// <summary>Convenience: the single-edit diff for <see cref="TryInsertBeforeTerminalOperatorEdit"/>.</summary>
    public static string? TryInsertBeforeTerminalOperator(CallSite site, string insertion)
    {
        var edit = TryInsertBeforeTerminalOperatorEdit(site, insertion);
        return edit is null ? null : BuildDiff([edit]);
    }

    private static LineEdit? TryInsertBeforeCall(CallSite site, string insertion, HashSet<string> names, Func<string, bool> lineFilter)
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

            var line = lines[i];
            if (!lineFilter(line))
            {
                continue;
            }

            var at = FindTopLevelCall(line, names);
            if (at < 0)
            {
                continue;
            }

            if (line.Contains(insertion, StringComparison.Ordinal))
            {
                return null;
            }

            return new LineEdit(site.FilePath!, i, line[..at] + insertion + line[at..]);
        }

        return null;
    }

    /// <summary>Index of the first <c>.Name(</c> on the line whose name is in <paramref name="names"/> and which sits at parenthesis depth 0 (not inside a lambda or another call), or -1.</summary>
    internal static int FindTopLevelCall(string line, HashSet<string> names)
    {
        var depth = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            switch (ch)
            {
                case '"':
                    i = SkipString(line, i);
                    continue;
                case '\'':
                    i = SkipChar(line, i);
                    continue;
                case '(':
                    depth++;
                    continue;
                case ')':
                    depth--;
                    continue;
                case '.' when depth == 0:
                {
                    var end = i + 1;
                    while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
                    {
                        end++;
                    }

                    var name = line[(i + 1)..end];
                    var paren = end;
                    while (paren < line.Length && char.IsWhiteSpace(line[paren]))
                    {
                        paren++;
                    }

                    if (name.Length > 0 && paren < line.Length && line[paren] == '(' && names.Contains(name))
                    {
                        return i;
                    }

                    continue;
                }
            }
        }

        return -1;
    }

    private static int SkipString(string text, int quoteIndex)
    {
        var verbatim = quoteIndex > 0 && text[quoteIndex - 1] == '@';
        for (var i = quoteIndex + 1; i < text.Length; i++)
        {
            if (text[i] == '\\' && !verbatim)
            {
                i++;
            }
            else if (text[i] == '"')
            {
                if (verbatim && i + 1 < text.Length && text[i + 1] == '"')
                {
                    i++;
                    continue;
                }

                return i;
            }
        }

        return text.Length;
    }

    private static int SkipChar(string text, int quoteIndex)
    {
        for (var i = quoteIndex + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == '\'')
            {
                return i;
            }
        }

        return text.Length;
    }

    /// <summary>
    /// Rewrites the per-item query inside a loop to read the navigation that an <c>Include</c> on the parent query now fills:
    /// <c>var orders = await db.Orders.Where(o =&gt; o.CustomerId == customer.Id).ToListAsync();</c> becomes <c>var orders = customer.Orders;</c>.
    /// Only the exact shape is handled: a root, optionally <c>AsNoTracking</c>/<c>TagWith</c>, exactly one predicate that is the key comparison and nothing else,
    /// and a terminal operator. Any other operator (<c>OrderBy</c>, <c>Take</c>, <c>Select</c>, <c>Include</c>, a second condition in the predicate)
    /// changes what the query returns, so the rewrite returns <c>null</c> and the caller reports a partial fix.
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
        var chain = ParseChain(expr);
        if (chain is null || chain.Count == 0)
        {
            return null;
        }

        string? predicate = null;
        var terminalIndex = chain.Count - 1;
        var (op, terminalArgs) = chain[terminalIndex];
        if (!s_terminals.Contains(op))
        {
            return null;
        }

        if (terminalArgs.Length > 0)
        {
            predicate = terminalArgs;
        }

        for (var i = 0; i < terminalIndex; i++)
        {
            var (name, args) = chain[i];
            if (s_neutralOperators.Contains(name))
            {
                continue;
            }

            if (name == "Where" && predicate is null)
            {
                predicate = args;
                continue;
            }

            return null; // anything else changes the result: not a shape we can replace with a navigation read
        }

        if (predicate is null)
        {
            return null;
        }

        var cmp = KeyLambda().Match(predicate);
        if (!cmp.Success)
        {
            return null;
        }

        var param = cmp.Groups["param"].Value;
        var (lo, lp, ro, rp) = (cmp.Groups["lo"].Value, cmp.Groups["lp"].Value, cmp.Groups["ro"].Value, cmp.Groups["rp"].Value);
        string? parent = null;
        if (lo == param && lp == filterProperty && ro != param)
        {
            parent = ro;
        }
        else if (ro == param && rp == filterProperty && lo != param)
        {
            parent = lo;
        }

        if (parent is null || parent is "this" or "db" or "context")
        {
            return null;
        }

        var lhs = statement.Groups["lhs"].Value.Trim();
        var indent = statement.Groups["indent"].Value;
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
            _ when s_referenceTerminals.Contains(op) && !navigationIsCollection => $"{indent}{lhs} = {navigationRead};",
            _ => null,
        };

        return replacement is null ? null : new LineEdit(site.FilePath!, index, replacement);
    }

    /// <summary>Splits <c>db.Orders.Where(o =&gt; ...).ToListAsync()</c> into its calls in order; <c>null</c> when the text is not a plain call chain.</summary>
    internal static List<(string Name, string Args)>? ParseChain(string expr)
    {
        var calls = new List<(string, string)>();
        var open = expr.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return null;
        }

        var head = expr[..open];
        var lastDot = head.LastIndexOf('.');
        if (lastDot < 0 || !IsIdentifier(head[(lastDot + 1)..]))
        {
            return null;
        }

        var name = head[(lastDot + 1)..];
        var i = open;
        while (true)
        {
            var close = MatchingParen(expr, i);
            if (close < 0)
            {
                return null;
            }

            calls.Add((name, expr[(i + 1)..close].Trim()));
            i = close + 1;
            if (i >= expr.Length)
            {
                return calls;
            }

            if (expr[i] != '.')
            {
                return null;
            }

            var next = expr.IndexOf('(', i);
            if (next < 0)
            {
                return null;
            }

            name = expr[(i + 1)..next].Trim();
            if (!IsIdentifier(name))
            {
                return null;
            }

            i = next;
        }
    }

    private static int MatchingParen(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '"':
                    i = SkipString(text, i);
                    break;
                case '\'':
                    i = SkipChar(text, i);
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    private static bool IsIdentifier(string s)
    {
        if (s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_'))
        {
            return false;
        }

        foreach (var ch in s)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
            {
                return false;
            }
        }

        return true;
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

    // "    var orders = await db.Orders.Where(...).ToListAsync();"  /  "    customer.Orders = await ...;"
    [GeneratedRegex(@"^(?<indent>\s*)(?<lhs>(?:var\s+)?[\w.]+)\s*=\s*(?<await>await\s+)?(?<expr>[^;]+?)\s*;\s*$")]
    private static partial Regex Statement();

    // Exactly "o => o.CustomerId == customer.Id" (either side order), nothing else in the lambda.
    [GeneratedRegex(@"^(?<param>\w+)\s*=>\s*(?<lo>\w+)\.(?<lp>\w+)\s*==\s*(?<ro>\w+)\.(?<rp>\w+)$")]
    private static partial Regex KeyLambda();
}
