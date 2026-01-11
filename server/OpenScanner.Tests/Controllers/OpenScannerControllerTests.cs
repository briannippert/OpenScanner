using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using OpenScanner.Server.Controllers;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using System.Text.Json;
using Xunit;

namespace OpenScanner.Tests.Controllers;

public class OpenScannerControllerTests
{
    private readonly Mock<IDatabase> _dbMock;
    private readonly Mock<IRadioSource> _radioMock;
    private readonly Mock<ISupportService> _supportMock;
    private readonly Mock<ILogger<OpenScannerController>> _loggerMock;
    private readonly OpenScannerController _controller;

    public OpenScannerControllerTests()
    {
        _dbMock = new Mock<IDatabase>();
        _radioMock = new Mock<IRadioSource>();
        _supportMock = new Mock<ISupportService>();
        _loggerMock = new Mock<ILogger<OpenScannerController>>();
        _controller = new OpenScannerController(_dbMock.Object, _radioMock.Object, _supportMock.Object, _loggerMock.Object);
    }

    [Fact]
    public void GetSystemInfo_ReturnsVersionInfo()
    {
        // Arrange
        var info = new Dictionary<string, string> { { "Commit", "abc" } };
        _supportMock.Setup(s => s.GetVersionInfo()).Returns(info);

        // Act
        var result = _controller.GetSystemInfo() as OkObjectResult;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(info, result.Value);
    }

    [Fact]
    public async Task GetSupportPackage_ReturnsFile()
    {
        // Arrange
        var fileContent = new byte[] { 1, 2, 3 };
        _supportMock.Setup(s => s.CreateSupportPackageAsync()).ReturnsAsync(fileContent);

        // Act
        var result = await _controller.GetSupportPackage() as FileContentResult;

        // Assert
        Assert.NotNull(result);
        Assert.Equal("application/zip", result.ContentType);
        Assert.Equal(fileContent, result.FileContents);
        Assert.StartsWith("openscanner_support_", result.FileDownloadName);
    }

    [Fact]
    public async Task GetAllChannels_ReturnsChannels()
    {
        // Arrange
        var channels = new List<Channel> { new Channel { Id = 1, AlphaTag = "Test" } };
        _dbMock.Setup(db => db.GetAllChannelsAsync()).ReturnsAsync(channels);

        // Act
        var result = await _controller.GetAllChannels();

        // Assert
        Assert.Single(result);
        Assert.Equal("Test", result.First().AlphaTag);
    }

    [Fact]
    public async Task AddChannel_ReturnsCreatedAndReloadsRadio()
    {
        // Arrange
        var channel = new Channel { AlphaTag = "New" };
        _dbMock.Setup(db => db.AddChannelAsync(channel)).ReturnsAsync(10);

        // Act
        var result = await _controller.AddChannel(channel) as CreatedAtActionResult;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(201, result.StatusCode);
        Assert.Equal(10, ((Channel)result.Value!).Id);
        _radioMock.Verify(r => r.ReloadChannels(), Times.Once);
    }

    [Fact]
    public async Task UpdateChannel_UpdatesAndReloadsRadio()
    {
        // Arrange
        var channel = new Channel { Id = 5, AlphaTag = "Updated" };

        // Act
        var result = await _controller.UpdateChannel(5, channel) as OkResult;

        // Assert
        Assert.NotNull(result);
        _dbMock.Verify(db => db.UpdateChannelAsync(It.Is<Channel>(c => c.Id == 5 && c.AlphaTag == "Updated")), Times.Once);
        _radioMock.Verify(r => r.ReloadChannels(), Times.Once);
    }

    [Fact]
    public async Task DeleteChannel_DeletesAndReloadsRadio()
    {
        // Act
        var result = await _controller.DeleteChannel(5) as OkResult;

        // Assert
        Assert.NotNull(result);
        _dbMock.Verify(db => db.DeleteChannelAsync(5), Times.Once);
        _radioMock.Verify(r => r.ReloadChannels(), Times.Once);
    }

    [Fact]
    public async Task GetHistory_ReturnsLogs()
    {
        // Arrange
        var logs = new List<CallLog> { new CallLog { Id = "1" } };
        _dbMock.Setup(db => db.GetHistoryAsync(100)).ReturnsAsync(logs);

        // Act
        var result = await _controller.GetHistory();

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public async Task GetYears_ReturnsYears()
    {
        // Arrange
        var years = new List<string> { "2023", "2024" };
        _dbMock.Setup(db => db.GetTransmissionYearsAsync()).ReturnsAsync(years);

        // Act
        var result = await _controller.GetYears();

        // Assert
        Assert.Equal(2, result.Count());
    }

    [Fact]
    public async Task GetMonths_ReturnsMonths()
    {
        // Arrange
        var months = new List<string> { "01", "02" };
        _dbMock.Setup(db => db.GetTransmissionMonthsAsync("2024")).ReturnsAsync(months);

        // Act
        var result = await _controller.GetMonths("2024");

        // Assert
        Assert.Equal(2, result.Count());
    }

    [Fact]
    public async Task GetDays_ReturnsDays()
    {
        // Arrange
        var days = new List<string> { "01", "15" };
        _dbMock.Setup(db => db.GetTransmissionDaysAsync("2024", "01")).ReturnsAsync(days);

        // Act
        var result = await _controller.GetDays("2024", "01");

        // Assert
        Assert.Equal(2, result.Count());
    }

    [Fact]
    public async Task GetChannelsForDay_ReturnsChannelData()
    {
        // Arrange
        var data = new List<dynamic> { new { Name = "Police", Count = 5 } };
        _dbMock.Setup(db => db.GetTransmissionChannelsAsync("2024", "01", "15")).ReturnsAsync(data);

        // Act
        var result = await _controller.GetChannelsForDay("2024", "01", "15");

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public async Task GetFilteredTransmissions_ReturnsLogs()
    {
        // Arrange
        var logs = new List<CallLog> { new CallLog { Id = "1" } };
        _dbMock.Setup(db => db.GetTransmissionsAsync("2024", "01", "15", "Police", 155.0)).ReturnsAsync(logs);

        // Act
        var result = await _controller.GetFilteredTransmissions("2024", "01", "15", "Police", 155.0);

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public async Task SearchTransmissions_ReturnsLogs()
    {
        // Arrange
        var logs = new List<CallLog> { new CallLog { Id = "1" } };
        _dbMock.Setup(db => db.SearchTransmissionsAsync("fire")).ReturnsAsync(logs);

        // Act
        var result = await _controller.SearchTransmissions("fire");

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public async Task DeleteTransmission_DeletesLog()
    {
        // Act
        var result = await _controller.DeleteTransmission("log_123") as OkResult;

        // Assert
        Assert.NotNull(result);
        _dbMock.Verify(db => db.DeleteTransmissionAsync("log_123"), Times.Once);
    }

    [Fact]
    public async Task ClearHistory_ClearsAll()
    {
        // Act
        var result = await _controller.ClearHistory() as OkResult;

        // Assert
        Assert.NotNull(result);
        _dbMock.Verify(db => db.ClearHistoryAsync(), Times.Once);
    }

    [Fact]
    public async Task GetFireTones_ReturnsTones()
    {
        // Arrange
        var tones = new List<FireToneSet> { new FireToneSet { Name = "Station 1" } };
        _dbMock.Setup(db => db.GetAllFireTonesAsync()).ReturnsAsync(tones);

        // Act
        var result = await _controller.GetFireTones();

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public async Task AddFireTone_ReturnsCreated()
    {
        // Arrange
        var tone = new FireToneSet { Name = "New Station" };
        _dbMock.Setup(db => db.AddFireToneAsync(tone)).ReturnsAsync(5);

        // Act
        var result = await _controller.AddFireTone(tone) as CreatedAtActionResult;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(5, ((FireToneSet)result.Value!).Id);
    }

    [Fact]
    public async Task UpdateFireTone_UpdatesTone()
    {
        // Arrange
        var tone = new FireToneSet { Id = 2, Name = "Updated" };

        // Act
        var result = await _controller.UpdateFireTone(2, tone) as OkResult;

        // Assert
        Assert.NotNull(result);
        _dbMock.Verify(db => db.UpdateFireToneAsync(It.Is<FireToneSet>(t => t.Id == 2 && t.Name == "Updated")), Times.Once);
    }

    [Fact]
    public async Task DeleteFireTone_DeletesTone()
    {
        // Act
        var result = await _controller.DeleteFireTone(2) as OkResult;

        // Assert
        Assert.NotNull(result);
        _dbMock.Verify(db => db.DeleteFireToneAsync(2), Times.Once);
    }

    [Fact]
    public async Task GetSettings_ReturnsDictionary()
    {
        // Arrange
        var settings = new Dictionary<string, string> { { "Key", "Value" } };
        _dbMock.Setup(db => db.GetAllSettingsAsync()).ReturnsAsync(settings);

        // Act
        var result = await _controller.GetSettings();

        // Assert
        Assert.Equal("Value", result["Key"]);
    }

    [Fact]
    public async Task UpdateSetting_UpdatesKey()
    {
        // Act
        var result = await _controller.UpdateSetting("Theme", "Dark") as OkResult;

        // Assert
        Assert.NotNull(result);
        _dbMock.Verify(db => db.SetSettingAsync("Theme", "Dark"), Times.Once);
    }

    [Fact]
    public void ControlScanner_Start_CallsRadioStart()
    {
        var body = JsonSerializer.SerializeToElement(new { action = "start" });
        var result = _controller.ControlScanner(body) as OkResult;
        
        Assert.NotNull(result);
        _radioMock.Verify(r => r.Start(), Times.Once);
    }

    [Fact]
    public void ControlScanner_Stop_CallsRadioStop()
    {
        var body = JsonSerializer.SerializeToElement(new { action = "stop" });
        var result = _controller.ControlScanner(body) as OkResult;
        
        Assert.NotNull(result);
        _radioMock.Verify(r => r.Stop(), Times.Once);
    }

    [Fact]
    public void ControlScanner_Hold_CallsRadioHold()
    {
        var body = JsonSerializer.SerializeToElement(new { action = "hold", frequency = 155.5 });
        var result = _controller.ControlScanner(body) as OkResult;
        
        Assert.NotNull(result);
        _radioMock.Verify(r => r.HoldFrequency(155.5), Times.Once);
    }
    
    [Fact]
    public void ControlScanner_SetSquelch_CallsRadioSetSquelch()
    {
        var body = JsonSerializer.SerializeToElement(new { action = "set_squelch", value = -40 });
        var result = _controller.ControlScanner(body) as OkResult;
        
        Assert.NotNull(result);
        _radioMock.Verify(r => r.SetSquelch(-40), Times.Once);
    }

    [Fact]
    public void ControlScanner_StartDump_CallsRadioStartDump()
    {
        var body = JsonSerializer.SerializeToElement(new { action = "start_dump", label = "test" });
        var result = _controller.ControlScanner(body) as OkResult;

        Assert.NotNull(result);
        _radioMock.Verify(r => r.StartDumping("test"), Times.Once);
    }

    [Fact]
    public void ControlScanner_StopDump_CallsRadioStopDump()
    {
        var body = JsonSerializer.SerializeToElement(new { action = "stop_dump" });
        var result = _controller.ControlScanner(body) as OkResult;

        Assert.NotNull(result);
        _radioMock.Verify(r => r.StopDumping(), Times.Once);
    }

    [Fact]
    public void ControlScanner_UnknownAction_ReturnsBadRequest()
    {
        var body = JsonSerializer.SerializeToElement(new { action = "invalid" });
        var result = _controller.ControlScanner(body) as BadRequestObjectResult;

        Assert.NotNull(result);
        Assert.Equal("Unknown action: invalid", result.Value);
    }
    
    [Fact]
    public void ControlScanner_MissingAction_ReturnsBadRequest()
    {
        var body = JsonSerializer.SerializeToElement(new { foo = "bar" });
        var result = _controller.ControlScanner(body) as BadRequestObjectResult;

        Assert.NotNull(result);
        Assert.Equal("Action is required", result.Value);
    }
}
