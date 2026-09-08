using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace QueryShape.Capture;

/// <summary>Finds the first user-code frame above EF Core / QueryShape, or parses EF Core's own <c>TagWithCallSite</c> tag.</summary>
internal static partial class CallSiteCapture
{
    private static readonly Assembly s_self = typeof(CallSiteCapture).Assembly;

    private static readonly string[] s_skippedNamespacePrefixes =
    [
        "Microsoft.",
        "System.",
        "Npgsql",
        "MySqlConnector",
        "Dapper",
        "Oracle.",
        "Pomelo.",
    ];

    /// <summary>Walks the stack. Expensive (needs file info); only call when call-site capture is enabled.</summary>
    public static CallSite? Capture()
    {
        try
        {
            var trace = new StackTrace(fNeedFileInfo: true);
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

                return new CallSite(frame.GetFileName(), frame.GetFileLineNumber(), DescribeMember(type, method));
            }
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

    private static bool ShouldSkip(Type type)
    {
        if (type.Assembly == s_self)
        {
            return true;
        }

        var ns = type.Namespace ?? string.Empty;
        foreach (var prefix in s_skippedNamespacePrefixes)
        {
            if (ns.StartsWith(prefix, StringComparison.Ordinal))
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
