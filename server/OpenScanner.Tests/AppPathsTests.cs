using OpenScanner.Server;
using Xunit;

namespace OpenScanner.Tests;

// Mutates a process-wide environment variable, so it must not run alongside
// tests that resolve paths.
[CollectionDefinition(nameof(AppPathsCollection), DisableParallelization = true)]
public class AppPathsCollection { }

[Collection(nameof(AppPathsCollection))]
public class AppPathsTests : IDisposable
{
    private readonly string? _original = Environment.GetEnvironmentVariable(AppPaths.HomeEnvVar);
    private readonly string _home = Path.Combine(Path.GetTempPath(), "openscanner-paths-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _original);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    [Fact]
    public void SourceCheckout_ResolvesRelativeToTheRepoRoot()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, null);
        var repoRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", ".."));

        Assert.False(AppPaths.IsInstalled);
        Assert.Null(AppPaths.LocalConfigFile);
        Assert.Equal(Path.Combine(repoRoot, "data"), AppPaths.DataDir);
        Assert.Equal(Path.Combine(repoRoot, "data", "recordings"), AppPaths.RecordingsDir);
        Assert.Equal(Path.Combine(repoRoot, "client", "dist"), AppPaths.ClientDist);
    }

    [Fact]
    public void Installed_KeepsPersistentDataOutsideTheAppDir()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);

        Assert.True(AppPaths.IsInstalled);
        Assert.Equal(Path.Combine(_home, "data", "recordings"), AppPaths.RecordingsDir);
        Assert.Equal(Path.Combine(_home, "data", "samples"), AppPaths.SamplesDir);
        Assert.Equal(Path.Combine(_home, "config", "appsettings.json"), AppPaths.LocalConfigFile);
        Assert.Equal(Path.Combine(_home, "whisper", "models", "ggml-small.en.bin"), AppPaths.WhisperModelPath("small.en"));
        Assert.Equal(Path.Combine(_home, "staging"), AppPaths.StagingDir);

        // Everything that ships in a release lives under app/, which updates replace.
        var app = Path.Combine(_home, "app");
        Assert.Equal(Path.Combine(app, "client", "dist"), AppPaths.ClientDist);
        Assert.Equal(Path.Combine(app, "whisper", "download-ggml-model.sh"), AppPaths.WhisperDownloadScript);
        Assert.Equal(Path.Combine(app, "whisper", "bin", "whisper-cli"), AppPaths.WhisperCli);
    }

    [Fact]
    public void Installed_PrefersADeviceBuiltWhisperOverTheBundledOne()
    {
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvVar, _home);
        var local = Path.Combine(_home, "whisper", "bin", "whisper-cli");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllText(local, "");

        Assert.Equal(local, AppPaths.WhisperCli);
        Assert.Equal(Path.Combine(_home, "whisper", "download-ggml-model.sh"), AppPaths.WhisperDownloadScript);
    }
}
