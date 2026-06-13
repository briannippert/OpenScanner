using Microsoft.AspNetCore.Mvc;
using Moq;
using OpenScanner.Server.Controllers;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using System.Text.Json;
using Xunit;

namespace OpenScanner.Tests.Controllers;

public class ScannerControllerTests
{
    private readonly Mock<IDatabase> _dbMock;
    private readonly Mock<IRadioSource> _radioMock;
    private readonly ScannerController _controller;

    public ScannerControllerTests()
    {
        _dbMock = new Mock<IDatabase>();
        _radioMock = new Mock<IRadioSource>();
        _controller = new ScannerController(_dbMock.Object, _radioMock.Object);
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
    public async Task ControlScanner_Start_CallsRadioStart()
    {
        var body = JsonSerializer.SerializeToElement(new { action = "start" });
        var result = await _controller.ControlScanner(body) as OkResult;
        
        Assert.NotNull(result);
        _radioMock.Verify(r => r.Start(), Times.Once);
    }

    [Fact]
    public async Task ControlScanner_Stop_CallsRadioStop()
    {
        var body = JsonSerializer.SerializeToElement(new { action = "stop" });
        var result = await _controller.ControlScanner(body) as OkResult;
        
        Assert.NotNull(result);
        _radioMock.Verify(r => r.Stop(), Times.Once);
    }

    [Fact]
    public async Task ControlScanner_Hold_CallsRadioHold()
    {
        var body = JsonSerializer.SerializeToElement(new { action = "hold", frequency = 155.5 });
        var result = await _controller.ControlScanner(body) as OkResult;
        
        Assert.NotNull(result);
        _radioMock.Verify(r => r.HoldFrequency(155.5), Times.Once);
    }
    
    [Fact]
    public async Task ControlScanner_SetSquelch_CallsRadioSetSquelch()
    {
        var body = JsonSerializer.SerializeToElement(new { action = "set_squelch", value = -40 });
        var result = await _controller.ControlScanner(body) as OkResult;
        
        Assert.NotNull(result);
        _radioMock.Verify(r => r.SetSquelch(-40), Times.Once);
    }

    [Fact]
    public async Task ControlScanner_StartDump_CallsRadioStartDump()
    {
        var body = JsonSerializer.SerializeToElement(new { action = "start_dump", label = "test" });
        var result = await _controller.ControlScanner(body) as OkResult;

        Assert.NotNull(result);
        _radioMock.Verify(r => r.StartDumping("test"), Times.Once);
    }

    [Fact]
    public async Task ControlScanner_StopDump_CallsRadioStopDump()
    {
        var body = JsonSerializer.SerializeToElement(new { action = "stop_dump" });
        var result = await _controller.ControlScanner(body) as OkResult;

        Assert.NotNull(result);
        _radioMock.Verify(r => r.StopDumping(), Times.Once);
    }

    [Fact]
    public async Task ControlScanner_DebugSpectrum_CallsRadioStartDebugSpectrum()
    {
        var body = JsonSerializer.SerializeToElement(new { action = "debug_spectrum", frequency = 162.400 });
        var result = await _controller.ControlScanner(body) as OkResult;

        Assert.NotNull(result);
        _radioMock.Verify(r => r.StartDebugSpectrum(162.400), Times.Once);
    }

    [Fact]
    public async Task ControlScanner_UnknownAction_ReturnsBadRequest()
    {
        var body = JsonSerializer.SerializeToElement(new { action = "invalid" });
        var result = await _controller.ControlScanner(body) as BadRequestObjectResult;

        Assert.NotNull(result);
        Assert.Equal("Unknown action: invalid", result.Value);
    }
    
    [Fact]
    public async Task ControlScanner_MissingAction_ReturnsBadRequest()
    {
        var body = JsonSerializer.SerializeToElement(new { foo = "bar" });
        var result = await _controller.ControlScanner(body) as BadRequestObjectResult;

        Assert.NotNull(result);
        Assert.Equal("Action is required", result.Value);
    }
}
