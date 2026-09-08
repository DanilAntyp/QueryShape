using QueryShape.Testing;

namespace QueryShape.Cli.Tests;

public class GeneratedRegressionTests
{
    [Fact]
    [Trait("Category", "Slow")]
    public async Task Generated_test_compiles_fails_on_the_reproducer_and_passes_after_the_fix()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qs-generated-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var result = await QueryReduction.MinimizeAsync<IReadOnlyList<int>>("contains-42", [1, 2, 42, 3], QueryReduction.RemoveChunks, x => x.Count,
                (x, _) => Task.FromResult(x.Contains(42) ? ReductionTrial.Fail("contains-42") : ReductionTrial.Pass));
            result.Input.Should().Equal(42);
            var generated = await result.WriteRegressionAsync(directory, "Replay.Cases.StillFailsAsync");
            File.Exists(Path.ChangeExtension(generated, ".json")).Should().BeTrue();
            await File.WriteAllTextAsync(Path.Combine(directory, "Regression.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.9.0" />
                    <PackageReference Include="xunit" Version="2.9.3" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
                  </ItemGroup>
                </Project>
                """);
            var replay = Path.Combine(directory, "Replay.cs");
            var source = """
                namespace Replay;
                public static class Cases
                {
                    public static System.Threading.Tasks.Task<bool> StillFailsAsync(string inputJson, System.Threading.CancellationToken ct)
                        => System.Threading.Tasks.Task.FromResult(System.Array.IndexOf(System.Text.Json.JsonSerializer.Deserialize<int[]>(inputJson), 42) >= 0);
                }
                """;
            await File.WriteAllTextAsync(replay, source);
            var failing = await ProcessRunner.RunAsync("dotnet", ["test", "Regression.csproj", "--nologo", "-v", "minimal"], directory);
            failing.ExitCode.Should().Be(1, failing.StdOut + failing.StdErr);
            failing.StdOut.Should().Contain("QueryShape reduced failure still reproduces");
            await File.WriteAllTextAsync(replay, source.Replace("System.Array.IndexOf(System.Text.Json.JsonSerializer.Deserialize<int[]>(inputJson), 42) >= 0", "false"));
            var fixedRun = await ProcessRunner.RunAsync("dotnet", ["test", "Regression.csproj", "--no-restore", "--nologo", "-v", "minimal"], directory);
            fixedRun.ExitCode.Should().Be(0, fixedRun.StdOut + fixedRun.StdErr);
        }
        finally { Directory.Delete(directory, true); }
    }
}
