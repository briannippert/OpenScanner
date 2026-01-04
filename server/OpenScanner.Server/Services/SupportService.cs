using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

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

public class MemoryLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _logs = new();

    public ILogger CreateLogger(string categoryName) => new MemoryLogger(categoryName, _logs);

    public void Dispose() { }

    public IEnumerable<string> GetLogs() => _logs.ToArray();
}

public class SupportService
{
    private readonly IConfiguration _configuration;
    private readonly MemoryLoggerProvider _loggerProvider;
    private readonly IDatabase _db;
    private readonly RtlDevice _radio;
    private readonly GpsService _gps;

    public SupportService(IConfiguration configuration, ILoggerProvider loggerProvider, IDatabase db, RtlDevice radio, GpsService gps)
    {
        _configuration = configuration;
        _loggerProvider = (MemoryLoggerProvider)loggerProvider;
        _db = db;
        _radio = radio;
        _gps = gps;
    }

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
                    ScannerState = _radio.GetState(),
                    GpsStatus = _gps.GetLastLocation(),
                    DiskSpace = GetDiskSpaceInfo()
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
            catch { }

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
        catch { return "N/A"; }
    }
}
