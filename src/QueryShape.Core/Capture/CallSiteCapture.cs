using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace QueryShape.Capture;

/// <summary>One stack walk: the frame the query is attributed to, plus the user frames it was reached through.</summary>
/// <param name="Site">The frame to blame, or <c>null</c> when no user frame was found.</param>
/// <param name="Path">User frames, innermost first, at most <see cref="QueryShapeOptions.CallPathDepth"/> of them. Starts at the frame that issued the query, which is <paramref name="Site"/> unless infrastructure frames were skipped for attribution.</param>
internal readonly record struct CallStack(CallSite? Site, IReadOnlyList<CallSite> Path)
{
    /// <summary>Nothing found.</summary>
    public static readonly CallStack None = new(null, []);
}

/// <summary>Finds the user-code frames above EF Core / QueryShape, or parses EF Core's own <c>TagWithCallSite</c> tag.</summary>
internal static partial class CallSiteCapture
{
    /// <summary>Never walk further than this many user frames: the path is for reading, and deep recursion must not turn one query into a long walk.</summary>
    private const int MaxUserFrames = 8;

    /// <summary>QueryShape's own assemblies (never user code), by assembly name. The test projects and the sample app keep their names deliberately distinct.</summary>
    private static readonly string[] s_ownAssemblies =
    [
        "QueryShape.Core",
        "QueryShape.Testing",
        "QueryShape.Testing.Xunit",
        "QueryShape.Testing.NUnit",
        "QueryShape.Testing.MSTest",
        "QueryShape.AspNetCore",
        "QueryShape.OpenTelemetry",
    ];

    /// <summary>Frameworks and providers that sit between QueryShape and the user's code on the stack, by assembly-name prefix (not namespace: user code may live in a Microsoft.* or System.* namespace).</summary>
    private static readonly string[] s_skippedAssemblyPrefixes =
    [
        "Microsoft.",
        "System.",
        "netstandard",
        "mscorlib",
        "Npgsql",
        "MySqlConnector",
        "MySql.",
        "Dapper",
        "Oracle.",
        "Pomelo.",
        "SQLitePCLRaw",

        // Test runners: they call the user's test method, so they sit above it in the path the same way EF Core sits below it.
        // Only reachable now that the walk keeps more than one frame (ADR-0014).
        "xunit.",
        "nunit.framework",
        "NUnit3.",
        "TUnit.",
        "testhost",
    ];

    /// <summary>
    /// Walks the stack. Expensive (needs file info); only call when call-site capture is enabled.
    /// Frames of EF Core, the BCL and the providers are skipped; what is left is user code, innermost first.
    /// The attributed site is the first of those frames that has source information and is not
    /// <see cref="QueryShapeOptions.InfrastructurePrefixes">infrastructure</see>: a data-access library in between
    /// (a repository base class, a specification evaluator) ships without symbols, while the user's code was compiled with them.
    /// Without any source information anywhere, the first non-skipped frame is reported as is.
    /// </summary>
    public static CallStack Capture(QueryShapeOptions? options = null)
    {
        try
        {
            var depth = Math.Clamp(options?.CallPathDepth ?? 1, 1, MaxUserFrames);
            var infrastructure = options?.InfrastructurePrefixes;
            var trace = new StackTrace(fNeedFileInfo: true);
            var frames = new List<(CallSite Site, bool IsInfrastructure)>(depth);

            for (var i = 0; i < trace.FrameCount && frames.Count < MaxUserFrames; i++)
            {
                var frame = trace.GetFrame(i);
                var method = frame?.GetMethod();
                var type = method?.DeclaringType;
                if (method is null || type is null || frame is null || ShouldSkip(type))
                {
                    continue;
                }

                frames.Add((new CallSite(frame.GetFileName(), frame.GetFileLineNumber(), DescribeMember(type, method)), IsInfrastructure(type, infrastructure)));

                // Enough for the path, and the frame to blame is already among them: no reason to keep walking.
                if (frames.Count >= depth && frames.Exists(f => !f.IsInfrastructure && f.Site.FilePath is not null))
                {
                    break;
                }
            }

            if (frames.Count == 0)
            {
                return CallStack.None;
            }

            // Best available: user code with symbols, then user code, then anything with symbols, then the innermost frame.
            var site = frames.Find(f => !f.IsInfrastructure && f.Site.FilePath is not null).Site
                ?? frames.Find(f => !f.IsInfrastructure).Site
                ?? frames.Find(f => f.Site.FilePath is not null).Site
                ?? frames[0].Site;

            return new CallStack(site, frames.Take(depth).Select(f => f.Site).ToArray());
        }
        catch
        {
            // Stack inspection is best-effort.
        }

        return CallStack.None;
    }

    /// <summary>Whether a frame belongs to code that issues queries on behalf of its callers, so the query is attributed to the caller instead.</summary>
    private static bool IsInfrastructure(Type type, IList<string>? prefixes)
    {
        if (prefixes is null || prefixes.Count == 0)
        {
            return false;
        }

        var assembly = type.Assembly.GetName().Name;
        var fullName = type.FullName;
        for (var i = 0; i < prefixes.Count; i++)
        {
            var prefix = prefixes[i];
            if (prefix.Length == 0)
            {
                continue;
            }

            if ((assembly is not null && assembly.StartsWith(prefix, StringComparison.Ordinal))
                || (fullName is not null && fullName.StartsWith(prefix, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses EF Core's <c>TagWithCallSite()</c> tag (<c>File: /path/File.cs:42</c>) into a call site. Zero cost at query time.</summary>
    public static CallSite? FromTags(IReadOnlyList<string> tags)
    {
        foreach (var tag in tags)
        {
            var m = CallSiteTag().Match(tag);
            if (m.Success && int.TryParse(m.Groups["line"].Value, out var line))
            {
                var path = m.Groups["path"].Value;
                return new CallSite(path, line, string.Empty); // the tag carries file and line only; the member stays unknown
            }
        }

        return null;
    }

    /// <summary>Whether a tag is EF Core's <c>TagWithCallSite()</c> tag. Such tags become <see cref="CallSite"/>s and are not listed among a command's tags (they hold a machine-specific path).</summary>
    public static bool IsCallSiteTag(string tag) => CallSiteTag().IsMatch(tag);

    /// <summary>Removes call-site tags; returns the same list when there are none (no allocation on the common path).</summary>
    public static IReadOnlyList<string> WithoutCallSiteTags(IReadOnlyList<string> tags)
    {
        var any = false;
        foreach (var tag in tags)
        {
            if (IsCallSiteTag(tag))
            {
                any = true;
                break;
            }
        }

        return any ? tags.Where(t => !IsCallSiteTag(t)).ToArray() : tags;
    }

    internal static bool ShouldSkip(Type type)
    {
        var assembly = type.Assembly.GetName().Name ?? string.Empty;
        if (Array.IndexOf(s_ownAssemblies, assembly) >= 0)
        {
            return true;
        }

        foreach (var prefix in s_skippedAssemblyPrefixes)
        {
            if (assembly.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // Async/iterator state machines and lambda display classes belong to their declaring user type; keep them.
        return false;
    }

    private static string DescribeMember(Type type, MethodBase method)
    {
        // Compiler-generated shapes:
        //   OrderService+<GetOrdersAsync>d__3.MoveNext        -> OrderService.GetOrdersAsync   (async method)
        //   OrderService+<>c.<Map>b__0_1                      -> OrderService.Map              (lambda)
        //   OrderService+<>c+<<Map>b__0_1>d.MoveNext          -> OrderService.Map              (async lambda)
        //   OrderService+<>c__DisplayClass0_0.<Map>b__0        -> OrderService.Map              (closure lambda)
        var methodName = method.Name;
        string? logical = null;

        var userType = type;
        while (userType is not null && IsGenerated(userType.Name))
        {
            logical ??= LogicalName(userType.Name);
            userType = userType.DeclaringType;
        }

        if (method.Name == "MoveNext" || IsGenerated(method.Name))
        {
            logical = (IsGenerated(method.Name) ? LogicalName(method.Name) : null) ?? logical;
            if (logical is not null)
            {
                methodName = logical;
            }
        }

        if (methodName == ".ctor")
        {
            methodName = "ctor";
        }

        var typeName = (userType ?? type).Name;
        var tick = typeName.IndexOf('`', StringComparison.Ordinal);
        if (tick > 0)
        {
            typeName = typeName[..tick];
        }

        return typeName + "." + methodName;
    }

    private static bool IsGenerated(string name) => name.StartsWith('<');

    /// <summary>Innermost user identifier inside nested angle brackets: "&lt;&lt;Map&gt;b__0_1&gt;d" → "Map".</summary>
    internal static string? LogicalName(string generated)
    {
        var s = generated;
        while (s.Length > 0 && s[0] == '<')
        {
            var depth = 0;
            var close = -1;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '<')
                {
                    depth++;
                }
                else if (s[i] == '>' && --depth == 0)
                {
                    close = i;
                    break;
                }
            }

            if (close <= 1)
            {
                return null;
            }

            var inner = s[1..close];
            if (inner.Length == 0)
            {
                return null;
            }

            if (inner[0] != '<')
            {
                return inner;
            }

            s = inner;
        }

        return null;
    }

    [GeneratedRegex(@"^File:\s*(?<path>.+):(?<line>\d+)\s*$")]
    private static partial Regex CallSiteTag();
}
