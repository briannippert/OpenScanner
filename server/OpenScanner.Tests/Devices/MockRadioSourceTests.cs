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
    private readonly Mock<IDecoderFactory> _decoderFactoryMock = new();
    private readonly Mock<ITranscriptionService> _transcriptionServiceMock = new();
    private readonly Mock<IRecordingService> _recordingServiceMock = new();
    private readonly Mock<IChannelService> _channelServiceMock = new();

    public MockRadioSourceTests()
    {
        _gpsServiceMock = new Mock<GpsService>(new Mock<ILogger<GpsService>>().Object);
        _toneDetectorMock = new Mock<ToneDetector>(_dbMock.Object, new Mock<ILogger<ToneDetector>>().Object);
        
        // Setup default channel service mock behavior if needed
        _channelServiceMock.Setup(x => x.Channels).Returns(new List<Channel>());
    }

    [Fact]
    public void MockRadioSource_InitializesInScanningMode()
    {
        var source = new MockRadioSource(
            _loggerMock.Object, 
            _dbMock.Object, 
            _gpsServiceMock.Object, 
            _toneDetectorMock.Object, 
            _decoderFactoryMock.Object,
            _transcriptionServiceMock.Object,
            _recordingServiceMock.Object,
            _channelServiceMock.Object);
            
        source.Start();
        
        var state = source.GetState();
        Assert.Equal("SCANNING", state.Status);
        Assert.True(state.IsHardwareConnected);
    }

    [Fact]
    public void MockRadioSource_HoldFrequency_UpdatesState()
    {
        var source = new MockRadioSource(
            _loggerMock.Object, 
            _dbMock.Object, 
            _gpsServiceMock.Object, 
            _toneDetectorMock.Object, 
            _decoderFactoryMock.Object,
            _transcriptionServiceMock.Object,
            _recordingServiceMock.Object,
            _channelServiceMock.Object);

        // Setup channel for hold
        var testChannel = new Channel(155.0325, "Test", "Test Desc", "FM", "FM");
        _channelServiceMock.Setup(x => x.Channels).Returns(new List<Channel> { testChannel });

        source.HoldFrequency(155.0325);
        
        var state = source.GetState();
        Assert.Equal("MONITORING", state.Status);
        Assert.Equal(155.0325, state.CurrentFrequency);
        Assert.Equal(155.0325, state.ManualHoldFrequency);
    }

    [Fact]
    public void MockRadioSource_Stop_ResetsState()
    {
        var source = new MockRadioSource(
            _loggerMock.Object, 
            _dbMock.Object, 
            _gpsServiceMock.Object, 
            _toneDetectorMock.Object, 
            _decoderFactoryMock.Object,
            _transcriptionServiceMock.Object,
            _recordingServiceMock.Object,
            _channelServiceMock.Object);

        source.Start();
        source.Stop();
        
        var state = source.GetState();
        Assert.Equal("IDLE", state.Status);
        Assert.Null(state.CurrentFrequency);
    }

    [Fact]
    public void MockRadioSource_LoadsScenarioFile_OnInit()
    {
        var source = new MockRadioSource(
            _loggerMock.Object, 
            _dbMock.Object, 
            _gpsServiceMock.Object, 
            _toneDetectorMock.Object, 
            _decoderFactoryMock.Object,
            _transcriptionServiceMock.Object,
            _recordingServiceMock.Object,
            _channelServiceMock.Object);
        
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