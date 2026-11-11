using System.Diagnostics;
using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests.Services;

public class ProcessRunnerTests
{
    private static ProcessStartInfo Bash(string script)
    {
        var psi = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        return psi;
    }

    [Fact]
    public void LargeStderrBeforeStdoutDoesNotDeadlock()
    {
        // 1 MB to stderr (far over the ~64 KB pipe buffer) while stdout is still open, then a
        // little stdout. Reading stdout to the end first would hang forever.
        var task = Task.Run(() => ProcessRunner.Run(
            Bash("head -c 1000000 /dev/zero | tr '\\0' 'e' >&2; echo out"), 20_000));

        Assert.True(task.Wait(TimeSpan.FromSeconds(30)), "ProcessRunner.Run hung");
        var result = task.Result!;
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("out", result.StdOut.Trim());
        Assert.Equal(1_000_000, result.StdErr.Length);
    }

    [Fact]
    public void TimeoutKillsTheProcessAndReportsIt()
    {
        var sw = Stopwatch.StartNew();
        var result = ProcessRunner.Run(Bash("sleep 60"), 500)!;

        Assert.True(result.TimedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ReportsExitCode()
    {
        var result = ProcessRunner.Run(Bash("echo hi; exit 3"), 10_000)!;

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("hi", result.StdOut.Trim());
    }
}
