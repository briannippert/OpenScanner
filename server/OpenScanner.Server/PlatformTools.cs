namespace OpenScanner.Server;

/// <summary>
/// Resolves paths to external command-line tools (rtl_sdr, rtl_fm, ffmpeg,
/// dsd-fme, stdbuf) in a cross-platform way so OpenScanner can run on Linux
/// (Raspberry Pi) and macOS without hardcoded absolute paths.
///
/// Resolution order for each tool: the current PATH, then a list of well-known
/// install directories (Homebrew on Apple Silicon / Intel, then the standard
/// Linux locations). If the tool cannot be found, the bare name is returned so
/// the OS can still resolve it at exec time and produce a clear error.
/// </summary>
public static class PlatformTools
{
    // Directories checked in addition to PATH. Homebrew first (macOS), then the
    // historical Linux install locations.
    private static readonly string[] WellKnownDirs =
    {
        "/opt/homebrew/bin", // Homebrew, Apple Silicon
        "/usr/local/bin",    // Homebrew (Intel) / source builds (dsd-fme)
        "/usr/bin",
        "/bin",
    };

    private static readonly Dictionary<string, string> _cache = new();
    private static readonly object _lock = new();

    /// <summary>
    /// Returns an absolute path to <paramref name="tool"/> if found on PATH or
    /// in a well-known directory; otherwise returns <paramref name="tool"/>
    /// unchanged so it resolves via PATH at process-start time.
    /// </summary>
    public static string Resolve(string tool)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(tool, out var cached)) return cached;

            var resolved = FindOnDisk(tool) ?? tool;
            _cache[tool] = resolved;
            return resolved;
        }
    }

    private static string? FindOnDisk(string tool)
    {
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in pathDirs.Concat(WellKnownDirs))
        {
            var candidate = Path.Combine(dir, tool);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string RtlSdr => Resolve("rtl_sdr");
    public static string RtlFm => Resolve("rtl_fm");
    public static string Ffmpeg => Resolve("ffmpeg");
    public static string DsdFme => Resolve("dsd-fme");

    /// <summary>
    /// Returns a <c>stdbuf &lt;args&gt; </c> command prefix (with trailing
    /// space) for tuning pipe buffering, or an empty string when stdbuf is not
    /// available. macOS has no stdbuf by default; <c>gstdbuf</c> (Homebrew
    /// coreutils) is used when present.
    /// </summary>
    public static string Stdbuf(string args)
    {
        var bin = StdbufBin;
        return bin == null ? string.Empty : $"{bin} {args} ";
    }

    private static string? StdbufBin
    {
        get
        {
            foreach (var name in new[] { "stdbuf", "gstdbuf" })
            {
                var path = FindOnDisk(name);
                if (path != null) return path;
            }
            return null;
        }
    }
}
