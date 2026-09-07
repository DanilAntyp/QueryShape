using System.Reflection;
using System.Runtime.InteropServices;

namespace QueryShape.SampleApp.Tests;

/// <summary>
/// The in-process test host from Microsoft.AspNetCore.TestHost 8.x cannot serve JSON on a rolled-forward .NET 10 runtime
/// (System.Text.Json 9+ needs PipeWriter.UnflushedBytes). CI runs each target on its real runtime; locally these tests are skipped for a mismatched runtime.
/// </summary>
public static class RuntimeMatch
{
    public static string? SkipReason { get; } = Compute();

    private static string? Compute()
    {
        // The test assembly's own target (AppContext.TargetFrameworkName reports the test host's).
        var target = typeof(RuntimeMatch).Assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName ?? string.Empty;   // ".NETCoreApp,Version=v8.0"
        var idx = target.IndexOf("Version=v", StringComparison.Ordinal);
        if (idx < 0 || !Version.TryParse(target[(idx + 9)..], out var targetVersion))
        {
            return null;
        }

        return targetVersion.Major == Environment.Version.Major
            ? null
            : $"Sample-app tests need the matching ASP.NET Core runtime: built for .NET {targetVersion.Major}, running on {RuntimeInformation.FrameworkDescription}.";
    }
}

public sealed class RuntimeMatchedFactAttribute : FactAttribute
{
    public RuntimeMatchedFactAttribute() => Skip = RuntimeMatch.SkipReason;
}

public sealed class RuntimeMatchedTheoryAttribute : TheoryAttribute
{
    public RuntimeMatchedTheoryAttribute() => Skip = RuntimeMatch.SkipReason;
}
