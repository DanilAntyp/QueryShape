using System.Diagnostics;
using System.Text;

namespace QueryShape.Cli;

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Runs external processes (git, dotnet) and captures output.</summary>
internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?>? environment = null, TextWriter? echo = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        if (environment is not null)
        {
            foreach (var (k, v) in environment)
            {
                if (v is null)
                {
                    psi.Environment.Remove(k);
                }
                else
                {
                    psi.Environment[k] = v;
                }
            }
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stdout.AppendLine(e.Data);
            echo?.WriteLine("    " + e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public static async Task<string> GitAsync(string workingDirectory, params string[] args)
    {
        var r = await RunAsync("git", args, workingDirectory);
        if (!r.Success)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({r.ExitCode}): {r.StdErr.Trim()}");
        }

        return r.StdOut.Trim();
    }
}
