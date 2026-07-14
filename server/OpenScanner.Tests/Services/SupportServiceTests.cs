using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using OpenScanner.Server;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using OpenScanner.Server.Interfaces;
using Xunit;

namespace OpenScanner.Tests;

public class SupportServiceTests
{
    private readonly Mock<IConfiguration> _configMock = new();
    private readonly Mock<ILoggerProvider> _loggerProviderMock = new();
    private readonly Mock<IDatabase> _dbMock = new();
    private readonly Mock<IRadioSource> _radioMock = new();
    private readonly Mock<ITranscriptionService> _transcriptionMock = new();
    private readonly Mock<IRecordingService> _recordingMock = new();
    private readonly Mock<GpsService> _gpsMock;
    private readonly WebSocketBroadcaster _broadcaster;
    private readonly RecordingCleanupService _cleanup;

    public SupportServiceTests()
    {
        _gpsMock = new Mock<GpsService>(new Mock<ILogger<GpsService>>().Object);
        _broadcaster = new WebSocketBroadcaster(_radioMock.Object, new Mock<ILogger<WebSocketBroadcaster>>().Object);
        _cleanup = new RecordingCleanupService(_dbMock.Object, new Mock<ILogger<RecordingCleanupService>>().Object, new ConfigurationBuilder().Build());
        _recordingMock.Setup(r => r.ActiveRecordingIds).Returns(new List<string>());

        // Setup Logger Provider
        var memLogger = new MemoryLoggerProvider();
        _loggerProviderMock.As<ILoggerProvider>(); // Just a placeholder, we'll pass concrete memLogger
    }

    [Fact]
    public void GetVersionInfo_ReturnsDictionary()
    {
        // Arrange
        var memLogger = new MemoryLoggerProvider();
        var service = new SupportService(_configMock.Object, memLogger, _dbMock.Object, _radioMock.Object, _gpsMock.Object, _transcriptionMock.Object, _broadcaster, _recordingMock.Object, _cleanup);

        // Act
        var info = service.GetVersionInfo();

        // Assert
        Assert.NotNull(info);
        // It should at least contain Commit (either hash or Unknown)
        Assert.True(info.ContainsKey("Commit"));
    }

    [Fact]
    public async Task CreateSupportPackage_ContainsExpectedFiles()
    {
        // Arrange
        var memLogger = new MemoryLoggerProvider();
        var logger = memLogger.CreateLogger("Test");
        logger.LogInformation("Test Log Entry");

        var configSection = new Mock<IConfigurationSection>();
        configSection.Setup(x => x.Value).Returns("SecretValue");
        configSection.Setup(x => x.Key).Returns("MySecretKey");
        // Mocking IConfiguration is tedious, skipping strict config check for now

        _radioMock.Setup(r => r.GetState()).Returns(new ScannerState("IDLE", 0));
        _gpsMock.Object.OnGpsUpdate += (d) => { }; // trigger init

        var service = new SupportService(_configMock.Object, memLogger, _dbMock.Object, _radioMock.Object, _gpsMock.Object, _transcriptionMock.Object, _broadcaster, _recordingMock.Object, _cleanup);

        // Act
        var zipBytes = await service.CreateSupportPackageAsync();

        // Assert
        Assert.NotNull(zipBytes);
        Assert.True(zipBytes.Length > 0);

        using var ms = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        Assert.Contains(archive.Entries, e => e.Name == "application.log");
        Assert.Contains(archive.Entries, e => e.Name == "system_info.json");
        Assert.Contains(archive.Entries, e => e.Name == "config_summary.json");
        
        // Verify log content
        var logEntry = archive.GetEntry("application.log");
        using var reader = new StreamReader(logEntry!.Open());
        var logContent = await reader.ReadToEndAsync();
        Assert.Contains("Test Log Entry", logContent);

        // Verify system info content
        var sysEntry = archive.GetEntry("system_info.json");
        using var sysReader = new StreamReader(sysEntry!.Open());
        var sysContent = await sysReader.ReadToEndAsync();
        Assert.Contains("Uptime", sysContent);
        Assert.Contains("CpuLoad", sysContent);
        Assert.Contains("MemoryUsage", sysContent);
        Assert.Contains("Network", sysContent);
        Assert.Contains("RunningProcesses", sysContent);
    }

    [Fact]
    public void GetSystemStats_ReturnsStats_WithPlausibleTemperature()
    {
        // Arrange
        var memLogger = new MemoryLoggerProvider();
        _transcriptionMock.Setup(t => t.GetQueueStatus()).Returns(new TranscriptionQueueStatus(0, 0));
        var service = new SupportService(_configMock.Object, memLogger, _dbMock.Object, _radioMock.Object, _gpsMock.Object, _transcriptionMock.Object, _broadcaster, _recordingMock.Object, _cleanup);

        // Act
        var stats = service.GetSystemStats();

        // Assert
        Assert.NotNull(stats);
        // TempCelsius is null when no thermal sensor is available (e.g. macOS/CI without /sys/class/thermal);
        // when present it must be a plausible reading.
        if (stats.TempCelsius.HasValue)
            Assert.InRange(stats.TempCelsius.Value, 0.1, 200.0);
    }
}
