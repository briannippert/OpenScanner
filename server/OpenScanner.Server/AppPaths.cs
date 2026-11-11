namespace OpenScanner.Server;

/// <summary>
/// Single source of truth for where the app keeps things on disk.
///
/// Two layouts are supported:
/// <list type="bullet">
/// <item><b>Installed</b> — <c>OPENSCANNER_HOME</c> is set (by the systemd unit) to the
/// install root, e.g. <c>/opt/openscanner</c>. The release lives in <c>app/</c> and is
/// replaced wholesale on update; <c>data/</c>, <c>whisper/</c> and <c>config/</c> sit
/// beside it and persist.</item>
/// <item><b>Source checkout</b> — the variable is unset and the server runs from
/// <c>server/OpenScanner.Server</c>, so everything resolves relative to the repo root
/// two levels up (<c>dotnet run</c>, tests, macOS).</item>
/// </list>
/// Properties are evaluated on each access so tests can switch layouts.
/// </summary>
public static class AppPaths
{
    /// <summary>Environment variable naming the install root.</summary>
    public const string HomeEnvVar = "OPENSCANNER_HOME";

    /// <summary>The install root, or null when running from a source checkout.</summary>
    public static string? Home
    {
        get
        {
            var home = Environment.GetEnvironmentVariable(HomeEnvVar);
            return string.IsNullOrWhiteSpace(home) ? null : Path.GetFullPath(home);
        }
    }

    /// <summary>True when running from a release install rather than a source checkout.</summary>
    public static bool IsInstalled => Home != null;

    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", ".."));

    /// <summary>Directory holding the persistent <c>data/</c> tree.</summary>
    private static string Root => Home ?? RepoRoot;

    /// <summary>Database, recordings and IQ samples.</summary>
    public static string DataDir => Path.Combine(Root, "data");

    /// <summary>Recorded transmissions, served at <c>/audio</c>.</summary>
    public static string RecordingsDir => Path.Combine(DataDir, "recordings");

    /// <summary>Raw IQ sample dumps.</summary>
    public static string SamplesDir => Path.Combine(DataDir, "samples");

    /// <summary>The currently installed release (installed layout only).</summary>
    public static string AppDir => Path.Combine(Root, "app");

    /// <summary>Built React client.</summary>
    public static string ClientDist => IsInstalled
        ? Path.Combine(AppDir, "client", "dist")
        : Path.Combine(RepoRoot, "client", "dist");

    /// <summary>Optional machine-local config overrides that survive updates (installed layout only).</summary>
    public static string? LocalConfigFile =>
        Home is { } home ? Path.Combine(home, "config", "appsettings.json") : null;

    /// <summary>Where the updater downloads and unpacks a release before it is swapped in.</summary>
    public static string StagingDir => Path.Combine(Root, "staging");

    // Installed layout: the directory holding bin/whisper-cli and the model download
    // script. A device-built copy under whisper/ (written by the installer when the
    // release's prebuilt binary cannot run on this CPU) wins over the bundled one.
    private static string InstalledWhisperDir(string home)
    {
        var local = Path.Combine(home, "whisper");
        return File.Exists(Path.Combine(local, "bin", "whisper-cli")) ? local : Path.Combine(AppDir, "whisper");
    }

    /// <summary>The whisper-cli binary.</summary>
    public static string WhisperCli => Home is { } home
        ? Path.Combine(InstalledWhisperDir(home), "bin", "whisper-cli")
        : Path.Combine(FindWhisperCheckout(), "build", "bin", "whisper-cli");

    /// <summary>Directory holding <c>ggml-&lt;model&gt;.bin</c> files; persists across updates.</summary>
    public static string WhisperModelsDir => Home is { } home
        ? Path.Combine(home, "whisper", "models")
        : Path.Combine(FindWhisperCheckout(), "models");

    /// <summary>whisper.cpp's model download script (<c>&lt;model&gt; [models_path]</c>).</summary>
    public static string WhisperDownloadScript => Home is { } home
        ? Path.Combine(InstalledWhisperDir(home), "download-ggml-model.sh")
        : Path.Combine(FindWhisperCheckout(), "models", "download-ggml-model.sh");

    /// <summary>Full path of a model file by name.</summary>
    public static string WhisperModelPath(string modelName) =>
        Path.Combine(WhisperModelsDir, $"ggml-{modelName}.bin");

    // Source checkout only: walk up from the working directory to the cloned whisper.cpp.
    private static string FindWhisperCheckout()
    {
        var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 6 && currentDir != null; i++)
        {
            var probe = Path.Combine(currentDir.FullName, "whisper.cpp");
            if (Directory.Exists(probe)) return probe;
            currentDir = currentDir.Parent;
        }
        return Path.Combine(RepoRoot, "whisper.cpp");
    }
}
