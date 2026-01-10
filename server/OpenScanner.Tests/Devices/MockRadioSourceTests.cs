using Microsoft.Extensions.Logging;
using Moq;
using OpenScanner.Server;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Devices;
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

    [Fact]
    public void MockRadioSource_LoadsScenarioFile_OnInit()
    {
        // This relies on TestData/scenario.json being present in the output directory
        var source = new MockRadioSource(_loggerMock.Object, _dbMock.Object, _gpsServiceMock.Object, _toneDetectorMock.Object);
        
        // We can't directly check private _scenarioEvents, but we can verify it doesn't log an error 
        // and we can check if it picks up an event if we wait (though that's more of a scenario test).
        // Since we moved scenario.json to TestData, the constructor should find it.
        
        // verify no error logs
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
            Times.Never);
    }
}
