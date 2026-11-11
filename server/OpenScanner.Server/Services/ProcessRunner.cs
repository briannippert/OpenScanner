using System.Diagnostics;

namespace OpenScanner.Server.Services;

/// <summary>Result of <see cref="ProcessRunner.Run"/>.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>
/// Runs a short-lived child process and captures its output without risking a hang.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Starts <paramref name="psi"/> (stdout and stderr must be redirected), reads both streams
    /// concurrently, and waits at most <paramref name="timeoutMs"/>.
    ///
    /// Reading the streams one after the other is a classic deadlock: a child that writes more
    /// than a pipe buffer (~64 KB) to the stream we are not currently reading blocks forever,
    /// and so do we. ffmpeg writes progress and diagnostics to stderr, so a long clip could
    /// freeze the caller while stdout was being read to its end. On timeout the whole process
    /// tree is killed.
    /// </summary>
    /// <returns>The result, or null if the process could not be started.</returns>
    public static ProcessResult? Run(ProcessStartInfo psi, int timeoutMs)
    {
        using var proc = Process.Start(psi);
        if (proc == null) return null;

        var stdout = psi.RedirectStandardOutput ? proc.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        var stderr = psi.RedirectStandardError ? proc.StandardError.ReadToEndAsync() : Task.FromResult("");

        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(true); } catch (InvalidOperationException) { /* exited just now */ }
            proc.WaitForExit();
            // Killing closes the pipes, so the readers complete.
            return new ProcessResult(-1, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult(), TimedOut: true);
        }

        // WaitForExit(timeout) can return before redirected output is fully flushed; the
        // parameterless overload waits for EOF on the streams.
        proc.WaitForExit();
        return new ProcessResult(proc.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult(), TimedOut: false);
    }
}
