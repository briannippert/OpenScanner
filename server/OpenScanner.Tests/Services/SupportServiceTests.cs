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
    private readonly Mock<GpsService> _gpsMock;

    public SupportServiceTests()
    {
        _gpsMock = new Mock<GpsService>(new Mock<ILogger<GpsService>>().Object);
        
        // Setup Logger Provider
        var memLogger = new MemoryLoggerProvider();
        _loggerProviderMock.As<ILoggerProvider>(); // Just a placeholder, we'll pass concrete memLogger
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

        var service = new SupportService(_configMock.Object, memLogger, _dbMock.Object, _radioMock.Object, _gpsMock.Object);

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
    }
}
