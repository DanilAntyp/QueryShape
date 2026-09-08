using System.Text.RegularExpressions;

namespace QueryShape.Cli.Llm;

/// <summary>Pulls a unified diff out of a model response and sanity-checks it before anything touches a worktree.</summary>
internal static partial class LlmPatch
{
    /// <summary>The model's way to decline.</summary>
    public const string CannotMarker = "CANNOT";

    /// <summary>Returns the diff text (LF line endings, trailing newline) or <c>null</c> when the response holds no unified diff.</summary>
    public static string? Extract(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        var text = response.Replace("\r\n", "\n", StringComparison.Ordinal);
        var fenced = Fence().Match(text);
        if (fenced.Success)
        {
            text = fenced.Groups["body"].Value;
        }

        var start = text.IndexOf("--- a/", StringComparison.Ordinal);
        var alt = text.IndexOf("diff --git ", StringComparison.Ordinal);
        if (alt >= 0 && (start < 0 || alt < start))
        {
            start = alt;
        }

        if (start < 0)
        {
            return null;
        }

        var diff = text[start..].TrimEnd() + "\n";
        return diff.Contains("\n+++ b/", StringComparison.Ordinal) && diff.Contains("\n@@ ", StringComparison.Ordinal) ? diff : null;
    }

    /// <summary>Paths the diff touches (from <c>+++ b/</c> lines).</summary>
    public static IReadOnlyList<string> TouchedPaths(string diff)
        => PlusPath().Matches(diff).Select(m => m.Groups["path"].Value.Trim()).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Every touched path must be an existing file inside the repository; returns the problem, or <c>null</c> when fine.</summary>
    public static string? Validate(string diff, string repoRoot)
    {
        var paths = TouchedPaths(diff);
        if (paths.Count == 0)
        {
            return "the diff names no files (+++ b/ lines are missing)";
        }

        var root = Path.GetFullPath(repoRoot);
        foreach (var p in paths)
        {
            if (p.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(p))
            {
                return $"the diff touches a path outside the repository: {p}";
            }

            var full = Path.GetFullPath(Path.Combine(root, p));
            if (!full.StartsWith(root, StringComparison.Ordinal))
            {
                return $"the diff touches a path outside the repository: {p}";
            }

            if (!File.Exists(full))
            {
                return $"the diff touches a file that does not exist: {p}";
            }
        }

        return null;
    }

    [GeneratedRegex(@"```(?:diff|patch)?\s*\n(?<body>.*?)```", RegexOptions.Singleline)]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\+\+\+ b/(?<path>[^\n\t]+)", RegexOptions.Multiline)]
    private static partial Regex PlusPath();
}
