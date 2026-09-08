namespace QueryShape.Internal;

/// <summary>
/// Whether this process is running tests. Decides the defaults of <see cref="QueryShapeOptions.CaptureCallSites"/> and
/// <see cref="QueryShapeOptions.ReadSourceFiles"/>: stack walks and source reads are on in tests and off in production (CLAUDE.md sections 4.1 and 9).
/// Detection looks once for a loaded test-framework assembly; it never throws.
/// </summary>
internal static class TestEnvironment
{
    private static readonly string[] s_frameworkAssemblies =
    [
        "xunit.core",
        "xunit.v3.core",
        "nunit.framework",
        "Microsoft.VisualStudio.TestPlatform.TestFramework",
        "TUnit.Core",
    ];

    private static readonly Lazy<bool> s_isTestProcess = new(Detect);

    /// <summary><c>true</c> when a test framework (xUnit, NUnit, MSTest, TUnit) is loaded in the current process.</summary>
    public static bool IsTestProcess => s_isTestProcess.Value;

    private static bool Detect()
    {
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name is not null && Array.IndexOf(s_frameworkAssemblies, name) >= 0)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Reflection over the loaded assemblies is best-effort.
        }

        return false;
    }
}
