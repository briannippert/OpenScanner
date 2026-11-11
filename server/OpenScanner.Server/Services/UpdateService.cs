using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

/// <summary>
/// In-UI self-updater. Compares the running build against the latest GitHub release
/// and, on request, applies it from the release's prebuilt artifact: download the
/// tarball for this CPU architecture → verify it against the release's
/// <c>SHA256SUMS</c> → unpack into a staging dir → hand off to the new release's
/// <c>apply_update.sh</c>, which runs detached, swaps the app directory and restarts
/// the systemd unit. Progress streams to clients over the control WebSocket as
/// <c>UPDATE_PROGRESS</c>, and a snapshot is available via <see cref="GetStatus"/>.
/// </summary>
public class UpdateService : IUpdateService, IHostedService
{
    private const string RepoSlug = "briannippert/OpenScanner";
    private const string ServiceUnit = "openscanner";
    private const string ChecksumsAsset = "SHA256SUMS";
    /// <summary>Top-level directory inside every release tarball.</summary>
    private const string ArchiveRoot = "openscanner";
    private const int MaxLogLines = 2000;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    private readonly ILogger<UpdateService> _logger;
    private readonly WebSocketBroadcaster _broadcaster;
    private readonly ISupportService _support;
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _latestReleaseUrl;

    private readonly object _lock = new();
    private readonly List<string> _log = new();
    private UpdateState _state = UpdateState.Idle;
    private bool _running;
    private string? _phase;
    private string? _error;

    // Latest known release / comparison, refreshed by CheckAsync.
    private string _currentVersion = "";
    private string _currentCommit = "";
    private ReleaseInfo? _latest;
    private bool _updateAvailable;
    private string? _lastCheckedUtc;

    private CancellationTokenSource? _periodicCts;

    public UpdateService(
        ILogger<UpdateService> logger,
        WebSocketBroadcaster broadcaster,
        ISupportService support,
        IHttpClientFactory httpFactory,
        IConfiguration config)
    {
        _logger = logger;
        _broadcaster = broadcaster;
        _support = support;
        _httpFactory = httpFactory;
        // Overridable so a fork or mirror can point devices at its own releases.
        var configured = config["Update:LatestReleaseUrl"];
        _latestReleaseUrl = string.IsNullOrWhiteSpace(configured)
            ? $"https://api.github.com/repos/{RepoSlug}/releases/latest"
            : configured;
    }

    /// <summary>A GitHub release and its downloadable assets (name → URL).</summary>
    internal sealed record ReleaseInfo(
        string? Tag, string? Name, string? Notes, string? Url, IReadOnlyDictionary<string, string> Assets);

    // MARK: - Pure helpers

    /// <summary>
    /// Name of the release tarball for a platform, or null when no artifact is
    /// published for it (anything other than 64-bit Linux).
    /// </summary>
    internal static string? AssetNameFor(bool isLinux, Architecture arch)
    {
        if (!isLinux) return null;
        return arch switch
        {
            Architecture.Arm64 => "openscanner-linux-arm64.tar.gz",
            Architecture.X64 => "openscanner-linux-x64.tar.gz",
            _ => null,
        };
    }

    private static string? LocalAssetName =>
        AssetNameFor(RuntimeInformation.IsOSPlatform(OSPlatform.Linux), RuntimeInformation.OSArchitecture);

    /// <summary>
    /// Looks up <paramref name="fileName"/> in <c>sha256sum</c> output
    /// (<c>&lt;hex&gt;  &lt;name&gt;</c> per line) and returns its lowercase hex digest,
    /// or null if the file is not listed or the digest is malformed.
    /// </summary>
    internal static string? FindChecksum(string? sums, string fileName)
    {
        if (string.IsNullOrEmpty(sums)) return null;
        foreach (var raw in sums.Split('\n'))
        {
            var parts = raw.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;
            // sha256sum prefixes the name with '*' in binary mode.
            var name = parts[1].Trim().TrimStart('*');
            if (!string.Equals(name, fileName, StringComparison.Ordinal)) continue;
            var hex = parts[0].ToLowerInvariant();
            return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="latestTag"/> is a newer version than the running
    /// <paramref name="current"/> build, comparing dotted numeric segments (leading
    /// 'v' and any '+build'/'-prerelease' suffix stripped).
    /// </summary>
    internal static bool IsNewerRelease(string? current, string? latestTag)
    {
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(latestTag)) return false;
        static int[] Parse(string v)
        {
            var core = v.TrimStart('v', 'V').Split('+', '-')[0];
            return core.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        }
        var c = Parse(current);
        var l = Parse(latestTag);
        for (int i = 0; i < Math.Max(c.Length, l.Length); i++)
        {
            var cv = i < c.Length ? c[i] : 0;
            var lv = i < l.Length ? l[i] : 0;
            if (lv != cv) return lv > cv;
        }
        return false;
    }

    /// <summary>Parses the GitHub "get a release" response.</summary>
    internal static ReleaseInfo ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? Str(string prop) => root.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

        var assets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url)) assets[name!] = url!;
            }
        }
        return new ReleaseInfo(Str("tag_name"), Str("name"), Str("body"), Str("html_url"), assets);
    }

    // MARK: - IHostedService (periodic availability check)

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _periodicCts = new CancellationTokenSource();
        var ct = _periodicCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch { return; }
            while (!ct.IsCancellationRequested)
            {
                try { await CheckAsync(force: false); }
                catch (Exception ex) { _logger.LogDebug(ex, "Periodic update check failed"); }
                try { await Task.Delay(CheckInterval, ct); } catch { break; }
            }
        }, ct);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _periodicCts?.Cancel();
        return Task.CompletedTask;
    }

    // MARK: - Public API

    public UpdateStatus GetStatus()
    {
        lock (_lock)
        {
            return new UpdateStatus
            {
                State = _state.ToString().ToLowerInvariant(),
                CurrentVersion = _currentVersion,
                CurrentCommit = _currentCommit,
                LatestTag = _latest?.Tag,
                LatestName = _latest?.Name ?? _latest?.Tag,
                ReleaseNotes = _latest?.Notes,
                ReleaseUrl = _latest?.Url,
                UpdateAvailable = _updateAvailable,
                Phase = _phase,
                Log = _log.ToArray(),
                Error = _error,
                LastCheckedUtc = _lastCheckedUtc,
            };
        }
    }

    public async Task<UpdateStatus> CheckAsync(bool force)
    {
        lock (_lock)
        {
            if (_running) return GetStatus();
            if (_state != UpdateState.Updating) _state = UpdateState.Checking;
        }

        var verInfo = _support.GetVersionInfo();
        var currentVersion = verInfo.GetValueOrDefault("Version", "");
        var currentCommit = verInfo.GetValueOrDefault("Commit", "");

        try
        {
            var release = await FetchLatestReleaseAsync();

            // A release is only installable here if it ships an artifact for this
            // machine. Source checkouts have nothing to compare against but the
            // version, so they still see that a newer release exists.
            var asset = LocalAssetName;
            var installable = !AppPaths.IsInstalled || (asset != null && release.Assets.ContainsKey(asset));
            var available = IsNewerRelease(currentVersion, release.Tag) && installable;

            lock (_lock)
            {
                _currentVersion = currentVersion;
                _currentCommit = currentCommit;
                _latest = release;
                _updateAvailable = available;
                _lastCheckedUtc = DateTime.UtcNow.ToString("o");
                _error = null;
                if (!_running) _state = available ? UpdateState.Available : UpdateState.Idle;
            }
            BroadcastState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            lock (_lock)
            {
                _currentVersion = currentVersion;
                _currentCommit = currentCommit;
                _lastCheckedUtc = DateTime.UtcNow.ToString("o");
                _error = $"Update check failed: {ex.Message}";
                // Do not flip to "available" on a failed check.
                if (!_running && _state == UpdateState.Checking) _state = _updateAvailable ? UpdateState.Available : UpdateState.Idle;
            }
        }

        return GetStatus();
    }

    public bool TryStartUpdate()
    {
        lock (_lock)
        {
            if (_running) return false;
            _running = true;
            _state = UpdateState.Updating;
            _phase = "start";
            _error = null;
            _log.Clear();
        }
        _ = Task.Run(RunUpdateAsync);
        return true;
    }

    // MARK: - Update pipeline

    private async Task RunUpdateAsync()
    {
        try
        {
            if (!AppPaths.IsInstalled)
            {
                Fail("This server is running from a source checkout, not a release install. Update it with git and rebuild.");
                return;
            }
            var assetName = LocalAssetName;
            if (assetName == null || !HasSystemd())
            {
                Fail("Self-update is only supported on 64-bit Linux installs managed by systemd.");
                return;
            }

            // Always re-read the release so we never install from a stale asset list.
            Emit("start", "Checking for the latest release…");
            ReleaseInfo release;
            try { release = await FetchLatestReleaseAsync(); }
            catch (Exception ex) { Fail($"Could not reach GitHub: {ex.Message}"); return; }
            lock (_lock) { _latest = release; }

            if (string.IsNullOrEmpty(release.Tag))
            { Fail("Could not determine the latest release to update to."); return; }
            if (!release.Assets.TryGetValue(assetName, out var assetUrl))
            { Fail($"Release {release.Tag} has no {assetName} artifact."); return; }
            if (!release.Assets.TryGetValue(ChecksumsAsset, out var sumsUrl))
            { Fail($"Release {release.Tag} has no {ChecksumsAsset}; refusing to install an unverified download."); return; }

            Emit("start", $"Updating to release {release.Tag}…");

            var staging = AppPaths.StagingDir;
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            var archivePath = Path.Combine(staging, assetName);

            using var cts = new CancellationTokenSource(DownloadTimeout);
            var http = CreateHttpClient(Timeout.InfiniteTimeSpan);

            Emit("download", $"Downloading {assetName}…");
            await DownloadAsync(http, assetUrl, archivePath, cts.Token);

            Emit("verify", "Verifying checksum…");
            var expected = FindChecksum(await http.GetStringAsync(sumsUrl, cts.Token), assetName);
            if (expected == null)
            { Fail($"{ChecksumsAsset} does not list {assetName}."); return; }
            string actual;
            await using (var fs = File.OpenRead(archivePath))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, cts.Token)).ToLowerInvariant();
            if (actual != expected)
            { Fail($"Checksum mismatch for {assetName} (expected {expected}, got {actual}). The running version is unchanged."); return; }
            Emit("verify", $"SHA-256 OK ({actual[..12]}…)");

            Emit("extract", "Unpacking…");
            await using (var fs = File.OpenRead(archivePath))
            await using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(gz, staging, overwriteFiles: true, cts.Token);
            File.Delete(archivePath);

            var newApp = Path.Combine(staging, ArchiveRoot);
            var applyScript = Path.Combine(newApp, "scripts", "apply_update.sh");
            if (!File.Exists(Path.Combine(newApp, "server", "OpenScanner.Server")) || !File.Exists(applyScript))
            { Fail("The downloaded release is missing expected files. The running version is unchanged."); return; }

            SetState(UpdateState.Success, "finalize");
            Emit("finalize", "Release verified. Swapping it in and restarting…");
            LaunchFinalizer(applyScript, newApp, AppPaths.Home!);
        }
        catch (OperationCanceledException)
        {
            Fail($"Update timed out after {DownloadTimeout.TotalMinutes:0} minutes. The running version is unchanged.");
        }
        catch (Exception ex)
        {
            Fail($"Update failed: {ex.Message}. The running version is unchanged.");
        }
        finally
        {
            bool handedOff;
            lock (_lock) { _running = false; handedOff = _state == UpdateState.Success; }
            // On success the apply script owns the staging dir; otherwise don't leave
            // a partial download or unpacked release behind.
            if (!handedOff && AppPaths.IsInstalled)
            {
                try { if (Directory.Exists(AppPaths.StagingDir)) Directory.Delete(AppPaths.StagingDir, recursive: true); }
                catch (Exception ex) { _logger.LogDebug(ex, "Could not clean up the update staging dir"); }
            }
        }
    }

    private async Task DownloadAsync(HttpClient http, string url, string destination, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destination);
        var buffer = new byte[1 << 16];
        long done = 0;
        var nextReport = 10;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total is > 0)
            {
                var pct = (int)(done * 100 / total.Value);
                if (pct >= nextReport)
                {
                    Emit("download", $"{pct}% ({done / 1048576} / {total.Value / 1048576} MB)");
                    nextReport = pct - pct % 10 + 10;
                }
            }
        }
        if (total is not > 0) Emit("download", $"Downloaded {done / 1048576} MB");
    }

    /// <summary>
    /// Runs the new release's apply script in its own transient systemd unit, so
    /// stopping the openscanner service during the swap does not kill the script.
    /// </summary>
    private void LaunchFinalizer(string applyScript, string newApp, string home)
    {
        try
        {
            var psi = new ProcessStartInfo("systemd-run")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // --no-block: return as soon as the unit is queued. Without it systemd-run
            // waits for the oneshot script to finish, and the script's first step is
            // stopping this service — which kills the waiting systemd-run (it is in
            // our cgroup), so it exited non-zero and a working update was reported
            // as "Failed to launch the update finalizer".
            psi.ArgumentList.Add("--no-block");
            psi.ArgumentList.Add("--collect");
            psi.ArgumentList.Add("--unit=openscanner-selfupdate");
            psi.ArgumentList.Add("--property=Type=oneshot");
            psi.ArgumentList.Add("/bin/bash");
            psi.ArgumentList.Add(applyScript);
            psi.ArgumentList.Add(newApp);
            psi.ArgumentList.Add(home);
            psi.ArgumentList.Add(ServiceUnit);
            using var proc = Process.Start(psi);
            // The script runs in its own cgroup, then stops us, swaps, and restarts.
            if (proc == null || !proc.WaitForExit(10_000) || proc.ExitCode != 0)
            {
                var err = proc is { HasExited: true } ? proc.StandardError.ReadToEnd().Trim() : "timed out";
                Fail($"Failed to launch the update finalizer: {err}");
                return;
            }
            _logger.LogInformation("Launched openscanner-selfupdate finalizer.");
        }
        catch (Exception ex)
        {
            Fail($"Failed to launch finalizer: {ex.Message}");
        }
    }

    // MARK: - GitHub

    private HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = timeout;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenScanner-Updater");
        return http;
    }

    private async Task<ReleaseInfo> FetchLatestReleaseAsync()
    {
        var http = CreateHttpClient(TimeSpan.FromSeconds(15));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var resp = await http.GetAsync(_latestReleaseUrl);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub returned {(int)resp.StatusCode}");

        return ParseRelease(await resp.Content.ReadAsStringAsync());
    }

    private static bool HasSystemd()
    {
        try
        {
            var psi = new ProcessStartInfo("systemctl", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(3000);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch { return false; }
    }

    // MARK: - State / logging

    private void Emit(string phase, string line)
    {
        string state;
        lock (_lock)
        {
            _phase = phase;
            _log.Add(line);
            if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
            state = _state.ToString().ToLowerInvariant();
        }
        _broadcaster.BroadcastUpdateProgress(phase, line, state);
    }

    private void SetState(UpdateState state, string? phase = null)
    {
        lock (_lock)
        {
            _state = state;
            if (phase != null) _phase = phase;
        }
        BroadcastState();
    }

    private void Fail(string message)
    {
        lock (_lock)
        {
            _state = UpdateState.Failed;
            _error = message;
        }
        Emit(_phase ?? "update", $"ERROR: {message}");
        BroadcastState();
    }

    /// <summary>Broadcasts a state-only progress message (empty line) so clients update their console/ribbon.</summary>
    private void BroadcastState()
    {
        string state, phase;
        lock (_lock) { state = _state.ToString().ToLowerInvariant(); phase = _phase ?? ""; }
        _broadcaster.BroadcastUpdateProgress(phase, "", state);
    }
}
