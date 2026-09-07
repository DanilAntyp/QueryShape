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
                return new CallSite(path, line, Path.GetFileNameWithoutExtension(path));
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
        // Compiler-generated: OrderService+<GetOrdersAsync>d__3.MoveNext  ->  OrderService.GetOrdersAsync
        //                     OrderService+<>c__DisplayClass0_0.<GetOrders>b__0  ->  OrderService.GetOrders
        var typeName = type.Name;
        var methodName = method.Name;

        if (type.IsNested && type.Name.StartsWith('<'))
        {
            var logical = GeneratedName().Match(type.Name);
            typeName = type.DeclaringType?.Name ?? typeName;
            if (logical.Success && methodName == "MoveNext")
            {
                methodName = logical.Groups["name"].Value;
            }
            else if (GeneratedName().Match(methodName) is { Success: true } lambda)
            {
                methodName = lambda.Groups["name"].Value;
            }
        }
        else if (GeneratedName().Match(methodName) is { Success: true } lambda)
        {
            methodName = lambda.Groups["name"].Value;
        }

        if (methodName == ".ctor")
        {
            methodName = "ctor";
        }

        var tick = typeName.IndexOf('`', StringComparison.Ordinal);
        if (tick > 0)
        {
            typeName = typeName[..tick];
        }

        return typeName + "." + methodName;
    }

    [GeneratedRegex(@"^<(?<name>[^>]+)>")]
    private static partial Regex GeneratedName();

    [GeneratedRegex(@"^File:\s*(?<path>.+):(?<line>\d+)\s*$")]
    private static partial Regex CallSiteTag();
}
