using Microsoft.AspNetCore.Http;
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
        _controller = new ScannerController(_dbMock.Object, _radioMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
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

    // --- REST endpoints ---

    [Fact]
    public void GetStatus_ReturnsRadioState()
    {
        var state = new ScannerState("SCANNING", -50);
        _radioMock.Setup(r => r.GetState()).Returns(state);

        var result = _controller.GetStatus();

        Assert.Equal("SCANNING", result.Status);
    }

    [Fact]
    public void SetPower_Enabled_CallsStart()
    {
        var result = _controller.SetPower(new PowerRequest(true)) as OkResult;

        Assert.NotNull(result);
        _radioMock.Verify(r => r.Start(), Times.Once);
        _radioMock.Verify(r => r.Stop(), Times.Never);
    }

    [Fact]
    public void SetPower_Disabled_CallsStop()
    {
        var result = _controller.SetPower(new PowerRequest(false)) as OkResult;

        Assert.NotNull(result);
        _radioMock.Verify(r => r.Stop(), Times.Once);
        _radioMock.Verify(r => r.Start(), Times.Never);
    }

    [Fact]
    public async Task Hold_HoldsFrequency()
    {
        _dbMock.Setup(db => db.GetAllChannelsAsync()).ReturnsAsync(new List<Channel>());

        var result = await _controller.Hold(new HoldRequest(155.5)) as OkResult;

        Assert.NotNull(result);
        _radioMock.Verify(r => r.HoldFrequency(155.5), Times.Once);
    }

    [Fact]
    public async Task Hold_UnAvoidsMatchingChannel()
    {
        var channel = new Channel { Id = 1, Frequency = 155.5, Avoid = true };
        _dbMock.Setup(db => db.GetAllChannelsAsync()).ReturnsAsync(new List<Channel> { channel });

        var result = await _controller.Hold(new HoldRequest(155.5)) as OkResult;

        Assert.NotNull(result);
        _dbMock.Verify(db => db.UpdateChannelAsync(It.Is<Channel>(c => c.Id == 1 && !c.Avoid)), Times.Once);
        _radioMock.Verify(r => r.ReloadChannels(), Times.Once);
        _radioMock.Verify(r => r.HoldFrequency(155.5), Times.Once);
    }

    [Fact]
    public void ReleaseHold_ResumesScan()
    {
        var result = _controller.ReleaseHold() as OkResult;

        Assert.NotNull(result);
        _radioMock.Verify(r => r.ResumeScan(), Times.Once);
    }

    [Fact]
    public void SetSquelch_CallsRadioSetSquelch()
    {
        var result = _controller.SetSquelch(new SquelchRequest(-40)) as OkResult;

        Assert.NotNull(result);
        _radioMock.Verify(r => r.SetSquelch(-40), Times.Once);
    }

    [Fact]
    public void AddAvoid_CallsRadioAvoidFrequency()
    {
        var result = _controller.AddAvoid(new AvoidRequest(155.5, 15)) as AcceptedResult;

        Assert.NotNull(result);
        _radioMock.Verify(r => r.AvoidFrequency(155.5, 15), Times.Once);
    }

    [Fact]
    public void AddAvoid_DefaultsDurationTo10()
    {
        var result = _controller.AddAvoid(new AvoidRequest(155.5)) as AcceptedResult;

        Assert.NotNull(result);
        _radioMock.Verify(r => r.AvoidFrequency(155.5, 10), Times.Once);
    }

    [Fact]
    public void StartIqDump_UsesProvidedLabel()
    {
        var result = _controller.StartIqDump(new IqDumpRequest("test")) as OkResult;

        Assert.NotNull(result);
        _radioMock.Verify(r => r.StartDumping("test"), Times.Once);
    }

    [Fact]
    public void StartIqDump_DefaultsLabelWhenMissing()
    {
        var result = _controller.StartIqDump(null) as OkResult;

        Assert.NotNull(result);
        _radioMock.Verify(r => r.StartDumping("sample"), Times.Once);
    }

    [Fact]
    public void StopIqDump_CallsRadioStopDump()
    {
        var result = _controller.StopIqDump() as OkResult;

        Assert.NotNull(result);
        _radioMock.Verify(r => r.StopDumping(), Times.Once);
    }

    [Fact]
    public void StartDebugSpectrum_CallsRadioStartDebugSpectrum()
    {
        var result = _controller.StartDebugSpectrum(new DebugSpectrumRequest(162.400, 30)) as OkResult;

        Assert.NotNull(result);
        _radioMock.Verify(r => r.StartDebugSpectrum(162.400, 30), Times.Once);
    }

    // --- Legacy /api/control shim (deprecated) ---
#pragma warning disable CS0618 // Intentionally testing the deprecated shim

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
#pragma warning restore CS0618
}
