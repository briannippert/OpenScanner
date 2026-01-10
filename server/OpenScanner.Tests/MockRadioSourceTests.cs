using Microsoft.Extensions.Logging;
using Moq;
using OpenScanner.Server;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests;

public class MockRadioSourceTests
{
    private readonly Mock<ILogger<MockRadioSource>> _loggerMock = new();
    private readonly Mock<IDatabase> _dbMock = new();
    private readonly Mock<GpsService> _gpsServiceMock;
    private readonly Mock<ToneDetector> _toneDetectorMock;

    public MockRadioSourceTests()
    {
        _gpsServiceMock = new Mock<GpsService>(new Mock<ILogger<GpsService>>().Object);
        _toneDetectorMock = new Mock<ToneDetector>(_dbMock.Object, new Mock<ILogger<ToneDetector>>().Object);
    }

    [Fact]
    public void MockRadioSource_InitializesInScanningMode()
    {
        var source = new MockRadioSource(_loggerMock.Object, _dbMock.Object, _gpsServiceMock.Object, _toneDetectorMock.Object);
        source.Start();
        
        var state = source.GetState();
        Assert.Equal("SCANNING", state.Status);
        Assert.True(state.IsHardwareConnected);
    }

    [Fact]
    public void MockRadioSource_HoldFrequency_UpdatesState()
    {
        var source = new MockRadioSource(_loggerMock.Object, _dbMock.Object, _gpsServiceMock.Object, _toneDetectorMock.Object);
        source.HoldFrequency(155.0325);
        
        var state = source.GetState();
        Assert.Equal("MONITORING", state.Status);
        Assert.Equal(155.0325, state.CurrentFrequency);
        Assert.Equal(155.0325, state.ManualHoldFrequency);
    }

    [Fact]
    public void MockRadioSource_Stop_ResetsState()
    {
        var source = new MockRadioSource(_loggerMock.Object, _dbMock.Object, _gpsServiceMock.Object, _toneDetectorMock.Object);
        source.Start();
        source.Stop();
        
        var state = source.GetState();
        Assert.Equal("IDLE", state.Status);
        Assert.Null(state.CurrentFrequency);
    }
}
