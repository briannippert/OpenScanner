using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpenScanner.Server;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Devices;
using OpenScanner.Server.Decoders;
using Xunit;

namespace OpenScanner.Tests;

public class MockScenarioTests
{
    private readonly ILogger<MockRadioSource> _logger;
    private readonly Mock<IDatabase> _dbMock = new();
    private readonly Mock<GpsService> _gpsServiceMock;
    private readonly Mock<ToneDetector> _toneDetectorMock;
    private readonly Mock<ITranscriptionService> _transcriptionServiceMock = new();
    private readonly Mock<IRecordingService> _recordingServiceMock = new();
    private readonly Mock<IChannelService> _channelServiceMock = new();
    private readonly MockRadioSource _source;

    public MockScenarioTests()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        _logger = loggerFactory.CreateLogger<MockRadioSource>();
        
        _gpsServiceMock = new Mock<GpsService>(new Mock<ILogger<GpsService>>().Object);
        _toneDetectorMock = new Mock<ToneDetector>(_dbMock.Object, new Mock<ILogger<ToneDetector>>().Object);
        
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug)); 
        services.AddTransient<P25>();
        services.AddTransient<NFM>();
        services.AddTransient<AM>();
        services.AddTransient<WFM>();
        services.AddSingleton<IDecoderFactory, DecoderFactory>();
        var serviceProvider = services.BuildServiceProvider();
        var decoderFactory = serviceProvider.GetRequiredService<IDecoderFactory>();

        // Setup default channels
        var channels = new List<Channel>
        {
            new Channel(155.000, "Police", "Test Channel", "P25", "RM", "123 NAC", "Law", "TEST1")
        };
        _dbMock.Setup(db => db.GetAllChannelsAsync()).ReturnsAsync(channels);
        _channelServiceMock.Setup(x => x.Channels).Returns(channels);
        
        _source = new MockRadioSource(
            _logger, 
            _dbMock.Object, 
            _gpsServiceMock.Object, 
            _toneDetectorMock.Object, 
            decoderFactory,
            _transcriptionServiceMock.Object,
            _recordingServiceMock.Object,
            _channelServiceMock.Object);
    }

    [Fact]
    public async Task Scenario_DetectsSignal_AndDecodesP25()
    {
        // Arrange
        var events = new List<ScenarioEvent>
        {
            new ScenarioEvent
            {
                Time = 1, // Start after 1 second
                Frequency = 155.000,
                AudioFile = "P25-C4FM-VC_IF.wav",
                Duration = 4,
                SourceId = 101,
                TargetId = 202,
                DecoderType = "P25"
            }
        };
        _source.SetScenario(events);
        
        var audioReceived = false;
        
        _source.OnAudio += (data) => audioReceived = true;

        // Act
        _source.Start();
        
        // Wait for event start (1s) + processing/decoding
        await Task.Delay(3000); 

        // Assert - Should be receiving
        var state = _source.GetState();
        Assert.Equal("RECEIVING", state.Status);
        
        _source.Stop();
    }

    [Fact]
    public async Task Scenario_DoesNotDetect_WhenOnDifferentHold()
    {
        // Arrange
        var events = new List<ScenarioEvent>
        {
            new ScenarioEvent { Time = 0.5, Frequency = 155.000, AudioFile = "test_48k.wav", Duration = 2 }
        };
        _source.SetScenario(events);
        
        // Mock channel service to find the channel we are holding on
        _channelServiceMock.Setup(x => x.Channels).Returns(new List<Channel> 
        { 
             new Channel(155.000, "Police", "Test Channel", "FM", "FM"),
             new Channel(156.000, "Marine", "Marine Channel", "FM", "FM")
        });
        
        _source.Start();
        
        // Allow service to start and settle in SCANNING
        await Task.Delay(100); 
        
        // Act
        _source.HoldFrequency(156.000); // Hold on different freq (Status -> MONITORING)
        
        // Wait for event time (0.5s)
        await Task.Delay(1500);

        // Assert
        var state = _source.GetState();
        Assert.Equal("MONITORING", state.Status);
        Assert.Equal(156.000, state.CurrentFrequency); // Should still be on hold freq
        
        _source.Stop();
    }

    [Fact]
    public async Task Scenario_PlaysGoldenSample_Correctly()
    {
        // Arrange
        var events = new List<ScenarioEvent>
        {
            new ScenarioEvent
            {
                Time = 0.5,
                Frequency = 155.000,
                AudioFile = "test_8k.wav",
                Duration = 4.8,
                SourceId = 999,
                TargetId = 888
            }
        };
        _source.SetScenario(events);
        
        _channelServiceMock.Setup(x => x.Channels).Returns(new List<Channel> 
        { 
             new Channel(155.000, "Police", "Test Channel", "FM", "FM")
        });

        var audioChunks = 0;
        _source.OnAudio += (data) => audioChunks++;

        // Act
        _source.Start();
        
        await Task.Delay(100); // Allow start
        _source.HoldFrequency(155.000); // Lock on immediately

        // Wait for event start time (0.5s) + playback (File is ~4.8s) + buffer
        await Task.Delay(6000);

        // Assert
        Assert.True(audioChunks > 10, $"Should have received audio chunks (Got {audioChunks})");
        
        var state = _source.GetState();
        Assert.Equal("MONITORING", state.Status);

        _source.Stop();
    }
}