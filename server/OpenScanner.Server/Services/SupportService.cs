using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenScanner.Server.Models;
using OpenScanner.Server.Interfaces;

namespace OpenScanner.Server.Services;

/// <summary>
/// Custom ILogger implementation that stores logs in memory for diagnostic purposes.
/// </summary>
public class MemoryLogger : ILogger
{
    private readonly string _categoryName;
    private readonly ConcurrentQueue<string> _logs;

    public MemoryLogger(string categoryName, ConcurrentQueue<string> logs)
    {
        _categoryName = categoryName;
        _logs = logs;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} [{logLevel}] [{_categoryName}] {formatter(state, exception)}";
        if (exception != null)
        {
            message += Environment.NewLine + exception.ToString();
        }

        _logs.Enqueue(message);

        // Keep last 1000 lines
        while (_logs.Count > 1000)
        {
            _logs.TryDequeue(out _);
        }
    }
}

/// <summary>
/// Logger provider that creates instances of <see cref="MemoryLogger"/>.
/// </summary>
public class MemoryLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _logs = new();

    public ILogger CreateLogger(string categoryName) => new MemoryLogger(categoryName, _logs);

    public void Dispose() { }

    /// <summary>
    /// Retrieves all logs currently stored in memory.
    /// </summary>
    public IEnumerable<string> GetLogs() => _logs.ToArray();
}

/// <summary>
/// Service responsible for gathering system diagnostics and creating support packages.
/// </summary>
public class SupportService : ISupportService
{
    private readonly IConfiguration _configuration;
    private readonly MemoryLoggerProvider _loggerProvider;
    private readonly IDatabase _db;
    private readonly IRadioSource _radio;
    private readonly GpsService _gps;
    private readonly ITranscriptionService _transcription;
    private readonly WebSocketBroadcaster _broadcaster;
    private readonly IRecordingService _recording;
    private readonly RecordingCleanupService _cleanup;

    // Previous CPU samples, retained so a percentage can be computed from the
    // delta between successive polls (this service is a singleton).
    private readonly object _cpuLock = new();
    private (long Idle, long Total)? _lastProcStat;
    private (DateTime When, TimeSpan Cpu)? _lastProcessCpu;

    // systemd units the debug page reports on, in display order.
    private static readonly string[] MonitoredUnits = { "openscanner", "gpsd" };

    /// <summary>
    /// Initializes a new instance of the <see cref="SupportService"/> class.
    /// </summary>
    public SupportService(IConfiguration configuration, ILoggerProvider loggerProvider, IDatabase db, IRadioSource radio, GpsService gps, ITranscriptionService transcription, WebSocketBroadcaster broadcaster, IRecordingService recording, RecordingCleanupService cleanup)
    {
        _configuration = configuration;
        _loggerProvider = (MemoryLoggerProvider)loggerProvider;
        _db = db;
        _radio = radio;
        _gps = gps;
        _transcription = transcription;
        _broadcaster = broadcaster;
        _recording = recording;
        _cleanup = cleanup;
    }

    /// <inheritdoc />
    public Dictionary<string, string> GetVersionInfo()
    {
        var info = new Dictionary<string, string>();
        
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            
            if (!string.IsNullOrEmpty(version))
            {
                var plusIndex = version.IndexOf('+');
                if (plusIndex >= 0)
                {
                    info["Commit"] = version.Substring(plusIndex + 1);
                    info["Version"] = version.Substring(0, plusIndex);
                }
                else
                {
                    info["Version"] = version;
                    info["Commit"] = "Unknown";
                }
            }
        }
        catch (Exception ex)
        {
            _loggerProvider.CreateLogger(nameof(SupportService)).LogDebug(ex, "Failed to get version info");
        }

        if (!info.ContainsKey("Commit") || info["Commit"] == "Unknown")
        {
            // Try to read from git directly if we are in a dev environment
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit();
                    if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    {
                        info["Commit"] = output;
                    }
                }
            }
            catch { }
        }

        info["CpuCores"] = Environment.ProcessorCount.ToString();
        return info;
    }

    /// <inheritdoc />
    public SystemStats GetSystemStats()
    {
        var cpu = GetCpuPercent();
        var (usedBytes, totalBytes) = GetMemoryBytes();
        var memPct = totalBytes > 0 ? (double)usedBytes / totalBytes * 100.0 : 0;

        return new SystemStats(
            Math.Round(cpu, 1),
            Math.Round(memPct, 1),
            usedBytes / 1024 / 1024,
            totalBytes / 1024 / 1024,
            _transcription.GetQueueStatus());
    }

    /// <inheritdoc />
    public async Task<DiagnosticsSnapshot> GetDiagnosticsAsync()
    {
        var state = _radio.GetState();
        var scanner = new ScannerSummary(
            state.Status,
            state.IsHardwareConnected,
            state.CurrentFrequency,
            state.CurrentSignalDb,
            state.SignalStrength,
            state.Gain,
            state.Squelch,
            state.IsAudioStreaming);

        var gps = new GpsDiagnostics(
            _gps.IsGpsdConnected,
            _gps.SecondsSinceLastFix.HasValue ? Math.Round(_gps.SecondsSinceLastFix.Value, 1) : null,
            _gps.LastKnownLocation);

        DbStats dbStats;
        try
        {
            dbStats = await _db.GetDbStatsAsync();
        }
        catch (Exception ex)
        {
            _loggerProvider.CreateLogger(nameof(SupportService)).LogDebug(ex, "Failed to read DB stats");
            dbStats = new DbStats(0, 0, 0, null, null);
        }

        var connections = new ConnectionStats(_broadcaster.ControlClientCount, _broadcaster.AudioClientCount);
        var recording = new RecordingActivity(_recording.ActiveRecordingCount, _recording.ActiveRecordingIds.ToList());
        var cleanup = new CleanupStatus(
            _cleanup.LastRunUtc?.ToString("o"),
            _cleanup.LastFreeBytes,
            _cleanup.TotalPurged);

        string? modelStatus = null;
        try { modelStatus = await _db.GetSettingAsync("TranscriptionModelStatus"); }
        catch (Exception ex) { _loggerProvider.CreateLogger(nameof(SupportService)).LogDebug(ex, "Failed to read model status"); }

        return new DiagnosticsSnapshot(
            GetUptime(),
            scanner,
            gps,
            dbStats,
            connections,
            recording,
            _radio.GetDiagnostics(),
            cleanup,
            modelStatus);
    }

    // System-wide CPU utilisation (0-100). Uses /proc/stat deltas on Linux and
    // falls back to this process's CPU time on other platforms (dev/macOS).
    private double GetCpuPercent()
    {
        lock (_cpuLock)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists("/proc/stat"))
            {
                try
                {
                    var first = File.ReadLines("/proc/stat").FirstOrDefault();
                    if (first != null && first.StartsWith("cpu "))
                    {
                        var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        // parts[0]="cpu"; then user nice system idle iowait irq softirq steal ...
                        var values = parts.Skip(1).Select(p => long.TryParse(p, out var v) ? v : 0).ToArray();
                        long idle = values.Length > 3 ? values[3] + (values.Length > 4 ? values[4] : 0) : 0; // idle + iowait
                        long total = values.Sum();

                        double pct = 0;
                        if (_lastProcStat is { } prev && total > prev.Total)
                        {
                            var dTotal = total - prev.Total;
                            var dIdle = idle - prev.Idle;
                            pct = (1.0 - (double)dIdle / dTotal) * 100.0;
                        }
                        _lastProcStat = (idle, total);
                        return Math.Clamp(pct, 0, 100);
                    }
                }
                catch (Exception ex)
                {
                    _loggerProvider.CreateLogger(nameof(SupportService)).LogDebug(ex, "Failed to read /proc/stat");
                }
            }

            // Cross-platform fallback: this process's CPU usage across all cores.
            try
            {
                using var proc = Process.GetCurrentProcess();
                var now = DateTime.UtcNow;
                var cpu = proc.TotalProcessorTime;
                double pct = 0;
                if (_lastProcessCpu is { } prev)
                {
                    var wallMs = (now - prev.When).TotalMilliseconds;
                    var cpuMs = (cpu - prev.Cpu).TotalMilliseconds;
                    if (wallMs > 0)
                        pct = cpuMs / (wallMs * Environment.ProcessorCount) * 100.0;
                }
                _lastProcessCpu = (now, cpu);
                return Math.Clamp(pct, 0, 100);
            }
            catch (Exception ex)
            {
                _loggerProvider.CreateLogger(nameof(SupportService)).LogDebug(ex, "Failed to compute process CPU");
                return 0;
            }
        }
    }

    // Returns (usedBytes, totalBytes) of physical memory. Uses /proc/meminfo on
    // Linux; elsewhere reports the managed heap against total available memory.
    private (long Used, long Total) GetMemoryBytes()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists("/proc/meminfo"))
        {
            try
            {
                long total = 0, available = 0;
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:")) total = ParseMeminfoKb(line);
                    else if (line.StartsWith("MemAvailable:")) available = ParseMeminfoKb(line);
                    if (total > 0 && available > 0) break;
                }
                if (total > 0)
                    return ((total - available) * 1024, total * 1024);
            }
            catch (Exception ex)
            {
                _loggerProvider.CreateLogger(nameof(SupportService)).LogDebug(ex, "Failed to read /proc/meminfo");
            }
        }

        try
        {
            var totalAvail = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            using var proc = Process.GetCurrentProcess();
            var used = proc.WorkingSet64;
            if (totalAvail <= 0) totalAvail = used;
            return (used, totalAvail);
        }
        catch (Exception ex)
        {
            _loggerProvider.CreateLogger(nameof(SupportService)).LogDebug(ex, "Failed to read process memory");
            return (0, 0);
        }
    }

    // "MemTotal:       8123456 kB" -> 8123456
    private static long ParseMeminfoKb(string line)
    {
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var kb) ? kb : 0;
    }

    /// <inheritdoc />
    public ServicesSnapshot GetServices()
    {
        return new ServicesSnapshot(GetServiceStatuses(), GetListeningPorts());
    }

    private List<ServiceStatus> GetServiceStatuses()
    {
        var result = new List<ServiceStatus>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            foreach (var unit in MonitoredUnits)
            {
                var output = RunCommand("systemctl", new[] { "show", unit, "-p", "ActiveState", "-p", "SubState", "-p", "MainPID", "-p", "Description" }, 4000);
                if (output == null)
                {
                    // systemctl missing entirely: stop probing further units.
                    break;
                }

                var props = ParseKeyValues(output);
                var active = props.GetValueOrDefault("ActiveState", "unknown");
                var sub = props.GetValueOrDefault("SubState", "");
                var pid = props.GetValueOrDefault("MainPID", "0");
                var desc = props.GetValueOrDefault("Description", unit);

                // A never-installed unit shows LoadState=not-found / ActiveState=inactive.
                var detail = pid != "0" && !string.IsNullOrEmpty(pid)
                    ? $"{desc} (pid {pid})"
                    : desc;
                result.Add(new ServiceStatus(unit, string.IsNullOrEmpty(sub) ? active : $"{active} ({sub})", detail));
            }
        }

        if (result.Count == 0)
        {
            // No systemd (e.g. macOS dev): report the app itself as the running service.
            using var self = Process.GetCurrentProcess();
            result.Add(new ServiceStatus("openscanner", "active (running)", $"OpenScanner Server (pid {self.Id})"));
        }

        return result;
    }

    private List<ListeningPort> GetListeningPorts()
    {
        var ports = new List<ListeningPort>();

        // Prefer `ss` on Linux; fall back to `lsof` (available on macOS/dev).
        var ss = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? RunCommand("ss", new[] { "-H", "-ltnp" }, 4000)
            : null;

        if (ss != null)
        {
            foreach (var line in ss.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // State Recv-Q Send-Q Local:Port Peer:Port [users:(("proc",pid=..))]
                if (cols.Length < 4) continue;
                var port = ExtractPort(cols[3]);
                if (port <= 0) continue;
                var proc = cols.Length >= 6 ? ExtractSsProcess(cols[5]) : "";
                ports.Add(new ListeningPort("tcp", port, proc));
            }
        }
        else
        {
            var lsof = RunCommand("lsof", new[] { "-nP", "-iTCP", "-sTCP:LISTEN" }, 4000);
            if (lsof != null)
            {
                foreach (var line in lsof.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
                {
                    var cols = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (cols.Length < 9) continue;
                    var port = ExtractPort(cols[8]); // NAME column, e.g. *:8080
                    if (port <= 0) continue;
                    ports.Add(new ListeningPort("tcp", port, cols[0]));
                }
            }
        }

        // Distinct by port, sorted for a stable display.
        return ports
            .GroupBy(p => p.Port)
            .Select(g => g.First())
            .OrderBy(p => p.Port)
            .ToList();
    }

    // Pull the trailing port out of an address token like "0.0.0.0:80", "[::]:443", "*:8080".
    private static int ExtractPort(string addr)
    {
        var idx = addr.LastIndexOf(':');
        if (idx < 0 || idx == addr.Length - 1) return -1;
        return int.TryParse(addr[(idx + 1)..], out var port) ? port : -1;
    }

    // users:(("dotnet",pid=1234,fd=200)) -> "dotnet"
    private static string ExtractSsProcess(string users)
    {
        var start = users.IndexOf("((\"", StringComparison.Ordinal);
        if (start < 0) return "";
        start += 3;
        var end = users.IndexOf('"', start);
        return end > start ? users[start..end] : "";
    }

    private static Dictionary<string, string> ParseKeyValues(string output)
    {
        var dict = new Dictionary<string, string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = line.IndexOf('=');
            if (eq > 0) dict[line[..eq]] = line[(eq + 1)..].Trim();
        }
        return dict;
    }

    /// <inheritdoc />
    public async Task<string> GetSystemdLogsAsync(int lines)
    {
        lines = Math.Clamp(lines, 1, 5000);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var output = await RunCommandAsync("journalctl", new[] { "-u", "openscanner", "-n", lines.ToString(), "--no-pager", "-o", "short-iso" }, 8000);
            if (!string.IsNullOrWhiteSpace(output))
                return output;
        }

        // Fallback (macOS dev, or journalctl unavailable): in-memory log buffer.
        var buffered = _loggerProvider.GetLogs().ToArray();
        var tail = buffered.Length > lines ? buffered[^lines..] : buffered;
        return string.Join(Environment.NewLine, tail);
    }

    // Runs a command and returns stdout, or null if the executable is missing /
    // fails to start. Non-zero exit codes still return whatever stdout was produced.
    private string? RunCommand(string fileName, string[] args, int timeoutMs)
    {
        return RunCommandAsync(fileName, args, timeoutMs).GetAwaiter().GetResult();
    }

    private async Task<string?> RunCommandAsync(string fileName, string[] args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return null;
            }

            return await stdoutTask;
        }
        catch (Exception ex)
        {
            _loggerProvider.CreateLogger(nameof(SupportService)).LogDebug(ex, "Command '{Cmd}' failed", fileName);
            return null;
        }
    }

    /// <summary>
    /// Creates a ZIP archive containing diagnostic information.
    /// </summary>
    /// <returns>Byte array of the ZIP file.</returns>
    public async Task<byte[]> CreateSupportPackageAsync()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            // 1. Logs
            var logEntry = archive.CreateEntry("application.log");
            using (var writer = new StreamWriter(logEntry.Open()))
            {
                foreach (var line in _loggerProvider.GetLogs())
                {
                    await writer.WriteLineAsync(line);
                }
            }

            // 2. System Info
            var sysInfoEntry = archive.CreateEntry("system_info.json");
            using (var writer = new StreamWriter(sysInfoEntry.Open()))
            {
                var info = new
                {
                    Timestamp = DateTime.UtcNow,
                    OS = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.OSArchitecture.ToString(),
                    Framework = RuntimeInformation.FrameworkDescription,
                    Uptime = GetUptime(),
                    CpuLoad = GetCpuLoad(),
                    MemoryUsage = GetMemoryUsage(),
                    Network = GetNetworkInfo(),
                    ScannerState = _radio.GetState(),
                    GpsStatus = _gps.GetLastLocation(),
                    DiskSpace = GetDiskSpaceInfo(),
                    RunningProcesses = GetRunningProcesses()
                };
                await writer.WriteAsync(JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            }

            // 3. Database Snapshot
            try
            {
                var dbPath = GetDatabasePath();
                if (File.Exists(dbPath))
                {
                    archive.CreateEntryFromFile(dbPath, "openscanner_backup.db");
                }
            }
            catch (Exception ex)
            {
                // We can't log to the archive we are building easily, but we can log to the system log
                // which might be captured in the next support package or if the user sees the console.
                // However, ILogger is available.
                var logger = _loggerProvider.CreateLogger(nameof(SupportService));
                logger.LogWarning(ex, "Failed to include database snapshot in support package");
            }

            // 4. Config (Masked)
            var configEntry = archive.CreateEntry("config_summary.json");
            using (var writer = new StreamWriter(configEntry.Open()))
            {
                var maskedConfig = new Dictionary<string, string>();
                foreach (var child in _configuration.AsEnumerable())
                {
                    if (string.IsNullOrEmpty(child.Value)) continue;
                    
                    var key = child.Key;
                    var val = child.Value;

                    if (key.Contains("Secret") || key.Contains("Key") || key.Contains("Password") || key.Contains("Token"))
                    {
                        val = "********";
                    }
                    maskedConfig[key] = val;
                }
                await writer.WriteAsync(JsonSerializer.Serialize(maskedConfig, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        return ms.ToArray();
    }

    private string GetDatabasePath()
    {
        var root = Directory.GetCurrentDirectory();
        var dataDir = Path.GetFullPath(Path.Combine(root, "../../data"));
        return Path.Combine(dataDir, "openscanner.db");
    }

    private static string GetRecordingsPath()
    {
        var root = Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(root, "../../data/recordings"));
    }

    /// <inheritdoc />
    public StorageInfo GetStorageInfo()
    {
        var logger = _loggerProvider.CreateLogger(nameof(SupportService));

        long recordingsBytes = 0;
        int recordingsCount = 0;
        try
        {
            var dir = GetRecordingsPath();
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        recordingsBytes += new FileInfo(file).Length;
                        recordingsCount++;
                    }
                    catch (Exception ex) { logger.LogDebug(ex, "Failed to stat recording {File}", file); }
                }
            }
        }
        catch (Exception ex) { logger.LogDebug(ex, "Failed to size recordings directory"); }

        long databaseBytes = 0;
        try
        {
            var dbPath = GetDatabasePath();
            if (File.Exists(dbPath)) databaseBytes = new FileInfo(dbPath).Length;
        }
        catch (Exception ex) { logger.LogDebug(ex, "Failed to size database"); }

        long diskFree = 0, diskTotal = 0;
        try
        {
            var drive = new DriveInfo(Directory.GetCurrentDirectory());
            diskFree = drive.AvailableFreeSpace;
            diskTotal = drive.TotalSize;
        }
        catch (Exception ex) { logger.LogDebug(ex, "Failed to read disk space"); }

        return new StorageInfo(recordingsBytes, recordingsCount, databaseBytes, diskFree, diskTotal);
    }

    private object GetDiskSpaceInfo()
    {
        try
        {
            var drive = new DriveInfo(Directory.GetCurrentDirectory());
            return new
            {
                Drive = drive.Name,
                TotalSizeGB = drive.TotalSize / 1024 / 1024 / 1024,
                FreeSpaceGB = drive.AvailableFreeSpace / 1024 / 1024 / 1024
            };
        }
        catch (Exception ex)
        {
            var logger = _loggerProvider.CreateLogger(nameof(SupportService));
            logger.LogDebug(ex, "Failed to get disk space info");
            return "N/A";
        }
    }

    private string GetUptime()
    {
        try
        {
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            return uptime.ToString(@"dd\.hh\:mm\:ss");
        }
        catch (Exception ex)
        {
            _loggerProvider.CreateLogger(nameof(SupportService)).LogError(ex, "Failed to get uptime");
            return "Unknown";
        }
    }

    private object GetCpuLoad()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                if (File.Exists("/proc/loadavg"))
                {
                    return File.ReadAllText("/proc/loadavg").Trim();
                }
            }
            catch (Exception ex)
            {
                _loggerProvider.CreateLogger(nameof(SupportService)).LogError(ex, "Failed to get CPU load");
            }
        }
        return "N/A";
    }

    private object GetMemoryUsage()
    {
        var result = new Dictionary<string, object>();
        var logger = _loggerProvider.CreateLogger(nameof(SupportService));
        
        // Process memory
        try
        {
            using var proc = Process.GetCurrentProcess();
            result["ProcessWorkingSetMB"] = proc.WorkingSet64 / 1024 / 1024;
            result["ProcessPrivateMemoryMB"] = proc.PrivateMemorySize64 / 1024 / 1024;
            result["GCTotalMemoryMB"] = GC.GetTotalMemory(false) / 1024 / 1024;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get process memory usage");
        }

        // System memory (Linux)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                if (File.Exists("/proc/meminfo"))
                {
                    var lines = File.ReadAllLines("/proc/meminfo").Take(5); // MemTotal, MemFree, MemAvailable, Buffers, Cached
                    foreach (var line in lines)
                    {
                        var parts = line.Split(':', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2)
                        {
                            result[parts[0].Trim()] = parts[1].Trim();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get system memory usage (Linux)");
            }
        }

        return result;
    }

    private object GetNetworkInfo()
    {
        var interfaces = new List<object>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                
                interfaces.Add(new
                {
                    Name = nic.Name,
                    Description = nic.Description,
                    Status = nic.OperationalStatus.ToString(),
                    Speed = nic.Speed > 0 ? (nic.Speed / 1000000) + " Mbps" : "Unknown",
                    Addresses = nic.GetIPProperties().UnicastAddresses.Select(ua => ua.Address.ToString()).ToList()
                });
            }
        }
        catch (Exception ex)
        {
            _loggerProvider.CreateLogger(nameof(SupportService)).LogError(ex, "Failed to get network info");
        }
        return interfaces;
    }

    private object GetRunningProcesses()
    {
        try
        {
            // Limit to top 50 by memory to avoid huge payloads
            var processes = Process.GetProcesses()
                .Select(p => {
                    try { return new { p.Id, p.ProcessName, MemoryMB = p.WorkingSet64 / 1024 / 1024 }; }
                    catch { return new { Id = p.Id, ProcessName = p.ProcessName, MemoryMB = 0L }; }
                })
                .OrderByDescending(p => p.MemoryMB)
                .Take(50)
                .ToList();
            
            return processes;
        }
        catch (Exception ex)
        {
            _loggerProvider.CreateLogger(nameof(SupportService)).LogError(ex, "Failed to list running processes");
            return $"Error listing processes: {ex.Message}";
        }
    }
}
