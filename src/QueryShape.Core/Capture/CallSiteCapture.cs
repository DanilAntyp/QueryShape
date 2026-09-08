using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace QueryShape.Capture;

/// <summary>Finds the first user-code frame above EF Core / QueryShape, or parses EF Core's own <c>TagWithCallSite</c> tag.</summary>
internal static partial class CallSiteCapture
{
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
    ];

    /// <summary>
    /// Walks the stack. Expensive (needs file info); only call when call-site capture is enabled.
    /// The first frame outside the skipped assemblies that has source information wins: a data-access library in between
    /// (a repository base class, a specification evaluator) ships without symbols, while the user's code was compiled with them.
    /// Without any source information anywhere, the first non-skipped frame is reported as is.
    /// </summary>
    public static CallSite? Capture()
    {
        try
        {
            var trace = new StackTrace(fNeedFileInfo: true);
            CallSite? withoutSource = null;
            for (var i = 0; i < trace.FrameCount; i++)
            {
                var frame = trace.GetFrame(i);
                var method = frame?.GetMethod();
                var type = method?.DeclaringType;
                if (method is null || type is null || frame is null)
                {
                    continue;
                }

                if (ShouldSkip(type))
                {
                    continue;
                }

                var file = frame.GetFileName();
                var site = new CallSite(file, frame.GetFileLineNumber(), DescribeMember(type, method));
                if (file is not null)
                {
                    return site;
                }

                withoutSource ??= site;
            }

            return withoutSource;
        }
        catch
        {
            // Stack inspection is best-effort.
        }

        return null;
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
