using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Moq;
using OpenScanner.Server;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Devices;
using Xunit;

namespace OpenScanner.Tests;

public class MockScenarioTests
{
    private readonly Mock<ILogger<MockRadioSource>> _loggerMock = new();
    private readonly Mock<IDatabase> _dbMock = new();
    private readonly Mock<GpsService> _gpsServiceMock;
    private readonly Mock<ToneDetector> _toneDetectorMock;
    private readonly MockRadioSource _source;

    public MockScenarioTests()
    {
        _gpsServiceMock = new Mock<GpsService>(new Mock<ILogger<GpsService>>().Object);
        _toneDetectorMock = new Mock<ToneDetector>(_dbMock.Object, new Mock<ILogger<ToneDetector>>().Object);
        
        // Setup default channels
        var channels = new List<Channel>
        {
            new Channel(155.000, "Police", "Test Channel", "P25", "RM", "123 NAC", "Law", "TEST1")
        };
        _dbMock.Setup(db => db.GetAllChannelsAsync()).ReturnsAsync(channels);
        
        _source = new MockRadioSource(_loggerMock.Object, _dbMock.Object, _gpsServiceMock.Object, _toneDetectorMock.Object);
    }

    [Fact]
    public async Task Scenario_DetectsSignal_AndPlaysAudio()
    {
        // Arrange
        var events = new List<ScenarioEvent>
        {
            new ScenarioEvent
            {
                Time = 1, // Start after 1 second
                Frequency = 155.000,
                AudioFile = "test_48k.wav",
                Duration = 2,
                SourceId = 101,
                TargetId = 202
            }
        };
        _source.SetScenario(events);
        
        var audioReceived = false;
        var callLogCreated = false;
        
        _source.OnAudio += (data) => audioReceived = true;
        _source.OnNewLog += (log) => callLogCreated = true;

        // Act
        _source.Start();
        var cts = new CancellationTokenSource();
        var task = _source.StartAsync(cts.Token);

        // Wait for event start (1s) + processing
        await Task.Delay(1500); 

        // Assert - Should be receiving
        var state = _source.GetState();
        Assert.Equal("RECEIVING", state.Status);
        Assert.Equal(155.000, state.CurrentFrequency);
        Assert.Equal(101, state.SourceID);
        
        // Wait for audio playback and completion (Duration 2s)
        await Task.Delay(2500);

        // Assert - Should have finished
        Assert.True(audioReceived, "Audio should have been received");
        Assert.True(callLogCreated, "CallLog should have been created");
        
        state = _source.GetState();
        Assert.Equal("SCANNING", state.Status); // Should go back to scanning

        cts.Cancel();
        try { await task; } catch (OperationCanceledException) { Console.WriteLine("Task cancelled as expected"); }
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
        
        var cts = new CancellationTokenSource();
        var task = _source.StartAsync(cts.Token);

        // Allow service to start and settle in SCANNING
        await Task.Delay(100); 
        
        // Act
        _source.HoldFrequency(156.000); // Hold on different freq (Status -> MONITORING)
        
        // Wait for event time (0.5s)
        await Task.Delay(1000);

        // Assert
        var state = _source.GetState();
        Assert.Equal("MONITORING", state.Status);
        Assert.Equal(156.000, state.CurrentFrequency); // Should still be on hold freq
        
        cts.Cancel();
        try { await task; } catch (OperationCanceledException) { Console.WriteLine("Task cancelled as expected"); }
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
                AudioFile = "police_48k.wav",
                Duration = 4.8,
                SourceId = 999,
                TargetId = 888
            }
        };
        _source.SetScenario(events);

        var audioChunks = 0;
        _source.OnAudio += (data) => audioChunks++;

        // Act
        var cts = new CancellationTokenSource();
        var task = _source.StartAsync(cts.Token);
        
        await Task.Delay(100); // Allow start
        _source.HoldFrequency(155.000); // Lock on immediately

        // Wait for event start time (0.5s) + playback (File is ~4.8s) + buffer
        await Task.Delay(6000);

        // Assert
        Assert.True(audioChunks > 50, $"Should have received audio chunks (Got {audioChunks})");
        
        var state = _source.GetState();
        Assert.Equal("MONITORING", state.Status);

        cts.Cancel();
        try { await task; } catch (OperationCanceledException) { Console.WriteLine("Task cancelled as expected"); }
    }
}
