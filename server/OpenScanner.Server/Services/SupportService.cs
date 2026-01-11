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

    /// <summary>
    /// Initializes a new instance of the <see cref="SupportService"/> class.
    /// </summary>
    public SupportService(IConfiguration configuration, ILoggerProvider loggerProvider, IDatabase db, IRadioSource radio, GpsService gps)
    {
        _configuration = configuration;
        _loggerProvider = (MemoryLoggerProvider)loggerProvider;
        _db = db;
        _radio = radio;
        _gps = gps;
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

        return info;
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
