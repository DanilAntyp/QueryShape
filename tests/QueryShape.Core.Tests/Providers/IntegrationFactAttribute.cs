using System.Runtime.InteropServices;

namespace QueryShape.Core.Tests.Providers;

/// <summary>Runs only when Docker is reachable; otherwise skipped with a reason. Tagged Category=Integration for filtering.</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (!Docker.IsAvailable)
        {
            Skip = "Docker is not available; Testcontainers-based provider tests are skipped.";
        }
    }
}

/// <summary>
/// Runs only when Docker is reachable and <c>QUERYSHAPE_ORACLE=1</c> is set. Oracle's image is measured in gigabytes and takes minutes to become
/// ready, so it stays out of the default run and is exercised by the manual provider workflow.
/// </summary>
public sealed class OracleFactAttribute : FactAttribute
{
    public OracleFactAttribute()
    {
        if (!Docker.IsAvailable)
        {
            Skip = "Docker is not available; Testcontainers-based provider tests are skipped.";
        }
        else if (Environment.GetEnvironmentVariable("QUERYSHAPE_ORACLE") is not ("1" or "true"))
        {
            Skip = "Set QUERYSHAPE_ORACLE=1 to run the Oracle provider test (multi-gigabyte image, minutes to start).";
        }
    }
}

internal static class Docker
{
    public static bool IsAvailable { get; } = Detect();

    private static bool Detect()
    {
        if (Environment.GetEnvironmentVariable("QUERYSHAPE_SKIP_DOCKER") is "1" or "true")
        {
            return false;
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE")))
        {
            return true;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return File.Exists(@"\\.\pipe\docker_engine");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return File.Exists("/var/run/docker.sock")
               || File.Exists(Path.Combine(home, ".docker", "run", "docker.sock"))
               || File.Exists(Path.Combine(home, ".colima", "default", "docker.sock"))
               || File.Exists(Path.Combine(home, ".rd", "docker.sock"));
    }
}
