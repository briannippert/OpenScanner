using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

/// <summary>
/// In-UI self-updater. Compares the running checkout against the latest GitHub
/// release and, on request, applies it: <c>git fetch --tags</c> → <c>git reset --hard &lt;tag&gt;</c>
/// → <c>dotnet build</c> into a staging dir → hand off a detached finalizer that swaps the
/// build in and restarts the systemd unit. Output streams to clients over the control
/// WebSocket as <c>UPDATE_PROGRESS</c>, and a snapshot is available via <see cref="GetStatus"/>.
/// </summary>
public class UpdateService : IUpdateService, IHostedService
{
    private const string RepoSlug = "briannippert/OpenScanner";
    private const string ServiceUnit = "openscanner";
    private const int MaxLogLines = 2000;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);

    private readonly ILogger<UpdateService> _logger;
    private readonly WebSocketBroadcaster _broadcaster;
    private readonly ISupportService _support;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;

    private readonly object _lock = new();
    private readonly List<string> _log = new();
    private UpdateState _state = UpdateState.Idle;
    private bool _running;
    private string? _phase;
    private string? _error;

    // Latest known release / comparison, refreshed by CheckAsync.
    private string _currentVersion = "";
    private string _currentCommit = "";
    private string? _latestTag;
    private string? _latestName;
    private string? _releaseNotes;
    private string? _releaseUrl;
    private int _commitsBehind;
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
        _config = config;
    }

    // MARK: - Paths / environment

    private static string RepoRoot => Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", ".."));
    private static string ServerProjDir => Path.Combine(RepoRoot, "server", "OpenScanner.Server");
    private static string PublishDir => Path.Combine(ServerProjDir, "bin", "Release", "net10.0", "publish");
    private static string StagingDir => Path.Combine(ServerProjDir, "bin", "Release", "net10.0", "_staging");
    private static string FinalizeScript => Path.Combine(RepoRoot, "scripts", "finalize_update.sh");
    private static string AppSettingsPath => Path.Combine(ServerProjDir, "appsettings.json");

    private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>Resolves the dotnet host: prefer a config override, then ~/.dotnet/dotnet (dev), else PATH.</summary>
    private string DotnetPath
    {
        get
        {
            var configured = _config["Update:DotnetPath"];
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dotnetHome = Path.Combine(home, ".dotnet", "dotnet");
            return File.Exists(dotnetHome) ? dotnetHome : "dotnet";
        }
    }

    /// <summary>
    /// Directory containing the <c>npm</c> (and co-located <c>node</c>) binary, or null
    /// if none can be found. The build's frontend step shells out to <c>npm</c>, but the
    /// service typically runs with a minimal PATH that omits an nvm-managed Node, so we
    /// resolve it explicitly and prepend it to the build process's PATH. Order: a
    /// <c>Update:NpmPath</c> config override, then the newest nvm Node under any home
    /// directory, then standard install locations.
    /// </summary>
    private string? ResolveNpmDir()
    {
        var configured = _config["Update:NpmPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var dir = File.Exists(configured) ? Path.GetDirectoryName(configured) : configured;
            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir!, "npm"))) return dir;
        }

        var homes = new List<string>();
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userHome)) homes.Add(userHome);
        if (Directory.Exists("/home"))
            try { homes.AddRange(Directory.GetDirectories("/home")); } catch { /* unreadable */ }
        homes.Add("/root");

        var candidates = new List<string>();
        foreach (var home in homes.Distinct())
        {
            var nvm = Path.Combine(home, ".nvm", "versions", "node");
            if (Directory.Exists(nvm))
                try { candidates.AddRange(Directory.GetDirectories(nvm).OrderByDescending(d => d, StringComparer.Ordinal).Select(d => Path.Combine(d, "bin"))); }
                catch { /* unreadable */ }
            candidates.Add(Path.Combine(home, ".local", "bin"));
        }
        candidates.AddRange(new[] { "/usr/local/bin", "/usr/bin", "/opt/homebrew/bin" });

        return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "npm")));
    }

    // Prefixes git args with `-c safe.directory=*` so the root-run service can operate
    // on a repo owned by another user (avoids git's "dubious ownership" refusal).
    private static string[] Git(params string[] args) =>
        new[] { "-c", "safe.directory=*" }.Concat(args).ToArray();

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
                LatestTag = _latestTag,
                LatestName = _latestName,
                ReleaseNotes = _releaseNotes,
                ReleaseUrl = _releaseUrl,
                CommitsBehind = _commitsBehind,
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
            if (_running) return GetStatusLocked();
            if (_state != UpdateState.Updating) _state = UpdateState.Checking;
        }

        var verInfo = _support.GetVersionInfo();
        var currentVersion = verInfo.GetValueOrDefault("Version", "");

        try
        {
            var (tag, name, notes, url) = await FetchLatestReleaseAsync();

            // Availability is decided by comparing the running build version to the
            // latest release tag. This is robust — it does not depend on the local git
            // graph being in sync (git may be unavailable/blocked when the service runs
            // as root on a differently-owned repo). The git steps below only provide the
            // informational commits-behind count and the current HEAD.
            var available = IsNewerRelease(currentVersion, tag);

            // Best-effort git metadata (commits behind + HEAD); may be empty if git is
            // unavailable, which is fine — availability is already decided above.
            await RunCaptureAsync("git", Git("fetch", "--tags", "--prune", "origin"), RepoRoot, 60_000);
            var head = (await RunCaptureAsync("git", Git("rev-parse", "HEAD"), RepoRoot, 15_000))?.Trim() ?? "";
            var behind = 0;
            if (!string.IsNullOrEmpty(tag))
            {
                var cnt = (await RunCaptureAsync("git", Git("rev-list", "--count", $"HEAD..{tag}"), RepoRoot, 15_000))?.Trim();
                int.TryParse(cnt, out behind);
            }

            lock (_lock)
            {
                _currentVersion = currentVersion;
                _currentCommit = string.IsNullOrEmpty(head) ? verInfo.GetValueOrDefault("Commit", "") : head;
                _latestTag = tag;
                _latestName = name ?? tag;
                _releaseNotes = notes;
                _releaseUrl = url;
                _commitsBehind = behind;
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
            var tag = _latestTag;
            if (string.IsNullOrEmpty(tag))
            {
                Emit("start", "No cached release; checking for the latest release…");
                await CheckAsync(force: true);
                tag = _latestTag;
            }
            if (string.IsNullOrEmpty(tag))
            {
                Fail("Could not determine the latest release to update to.");
                return;
            }

            Emit("start", $"Updating to release {tag}…");

            if (await RunStreamingAsync("git", Git("fetch", "--tags", "--prune", "origin"), RepoRoot, "fetch", 120_000) != 0)
            { Fail("git fetch failed."); return; }

            // Preserve the operator-configured PowerDMS department across the hard reset.
            var savedDept = TryReadPowerDmsDepartment();

            if (await RunStreamingAsync("git", Git("reset", "--hard", tag!), RepoRoot, "reset", 60_000) != 0)
            { Fail("git reset failed."); return; }

            if (!string.IsNullOrEmpty(savedDept) && RestorePowerDmsDepartment(savedDept!))
                Emit("reset", $"Preserved PowerDMS department \"{savedDept}\".");

            Emit("build", "Building server and client (this can take several minutes)…");
            var npmDir = ResolveNpmDir();
            Emit("build", npmDir != null
                ? $"Using npm from {npmDir}"
                : "Warning: could not locate npm; relying on the service PATH for the frontend build.");
            if (await RunStreamingAsync(DotnetPath, new[] { "build", "-c", "Release", "-o", StagingDir }, ServerProjDir, "build", 20 * 60_000, npmDir) != 0)
            { Fail("Build failed. The running version is unchanged."); return; }

            if (IsLinux && HasSystemd())
            {
                SetState(UpdateState.Success, "finalize");
                Emit("finalize", "Build succeeded. Swapping in the new build and restarting…");
                LaunchFinalizer();
            }
            else
            {
                SetState(UpdateState.Success, "finalize");
                Emit("finalize", "Build succeeded. No systemd detected (dev) — restart the server manually to run the new build.");
            }
        }
        catch (Exception ex)
        {
            Fail($"Update crashed: {ex.Message}");
        }
        finally
        {
            lock (_lock) { _running = false; }
        }
    }

    private void LaunchFinalizer()
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
            psi.ArgumentList.Add("--collect");
            psi.ArgumentList.Add("--unit=openscanner-selfupdate");
            psi.ArgumentList.Add("--property=Type=oneshot");
            psi.ArgumentList.Add("/bin/bash");
            psi.ArgumentList.Add(FinalizeScript);
            psi.ArgumentList.Add(StagingDir);
            psi.ArgumentList.Add(PublishDir);
            psi.ArgumentList.Add(ServiceUnit);
            using var proc = Process.Start(psi);
            // systemd-run registers the transient unit and returns immediately; the
            // finalizer (in its own cgroup) then stops us, swaps, and restarts.
            proc?.WaitForExit(10_000);
            _logger.LogInformation("Launched openscanner-selfupdate finalizer.");
        }
        catch (Exception ex)
        {
            Fail($"Failed to launch finalizer: {ex.Message}");
        }
    }

    // MARK: - GitHub

    private async Task<(string? tag, string? name, string? notes, string? url)> FetchLatestReleaseAsync()
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenScanner-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var resp = await http.GetAsync($"https://api.github.com/repos/{RepoSlug}/releases/latest");
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub returned {(int)resp.StatusCode}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        return (tag, name, notes, url);
    }

    // MARK: - PowerDMS preservation

    private string? TryReadPowerDmsDepartment()
    {
        try
        {
            if (!File.Exists(AppSettingsPath)) return null;
            var node = JsonNode.Parse(File.ReadAllText(AppSettingsPath));
            var dept = node?["PowerDMS"]?["Department"]?.GetValue<string>();
            return string.IsNullOrEmpty(dept) ? null : dept;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read PowerDMS department for preservation");
            return null;
        }
    }

    private bool RestorePowerDmsDepartment(string dept)
    {
        try
        {
            if (!File.Exists(AppSettingsPath)) return false;
            var node = JsonNode.Parse(File.ReadAllText(AppSettingsPath)) as JsonObject;
            if (node == null) return false;
            var powerDms = node["PowerDMS"] as JsonObject;
            if (powerDms == null) { powerDms = new JsonObject(); node["PowerDMS"] = powerDms; }
            if (string.Equals(powerDms["Department"]?.GetValue<string>(), dept, StringComparison.Ordinal)) return false;
            powerDms["Department"] = dept;
            File.WriteAllText(AppSettingsPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore PowerDMS department after reset");
            return false;
        }
    }

    // MARK: - Process helpers

    /// <summary>Runs a command, streaming each stdout/stderr line to clients and the log. Returns the exit code (-1 on timeout/failure to start).</summary>
    private async Task<int> RunStreamingAsync(string file, string[] args, string workingDir, string phase, int timeoutMs, string? extraPathDir = null)
    {
        Emit(phase, $"$ {Path.GetFileName(file)} {string.Join(' ', args)}");
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            // Prepend a directory to PATH so child tools invoked by the build (npm/node)
            // are found even when the service runs with a minimal PATH.
            if (!string.IsNullOrEmpty(extraPathDir))
            {
                var existing = psi.Environment.TryGetValue("PATH", out var p) && !string.IsNullOrEmpty(p)
                    ? p
                    : Environment.GetEnvironmentVariable("PATH");
                psi.Environment["PATH"] = string.IsNullOrEmpty(existing)
                    ? extraPathDir
                    : $"{extraPathDir}{Path.PathSeparator}{existing}";
            }

            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) Emit(phase, e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Emit(phase, e.Data); };
            if (!proc.Start()) { Emit(phase, "[failed to start process]"); return -1; }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                Emit(phase, $"[timed out after {timeoutMs / 1000}s]");
                return -1;
            }
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Emit(phase, $"[error: {ex.Message}]");
            return -1;
        }
    }

    /// <summary>Runs a command and returns stdout (or null), without streaming. Used for the availability check.</summary>
    private async Task<string?> RunCaptureAsync(string file, string[] args, string workingDir, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return null; }
            return await stdoutTask;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Capture command '{Cmd}' failed", file);
            return null;
        }
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

    private UpdateStatus GetStatusLocked()
    {
        return new UpdateStatus
        {
            State = _state.ToString().ToLowerInvariant(),
            CurrentVersion = _currentVersion,
            CurrentCommit = _currentCommit,
            LatestTag = _latestTag,
            LatestName = _latestName,
            ReleaseNotes = _releaseNotes,
            ReleaseUrl = _releaseUrl,
            CommitsBehind = _commitsBehind,
            UpdateAvailable = _updateAvailable,
            Phase = _phase,
            Log = _log.ToArray(),
            Error = _error,
            LastCheckedUtc = _lastCheckedUtc,
        };
    }
}
