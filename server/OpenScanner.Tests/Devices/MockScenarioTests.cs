using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Devices;
using Xunit;

namespace OpenScanner.Tests;

/// <summary>
/// Deterministic tests for <see cref="MockRadioSource"/>. Simulation time is driven
/// by a <see cref="FakeTimeProvider"/> and audio by <see cref="SyntheticAudioProvider"/>,
/// so a multi-second transmission is exercised by advancing the fake clock instantly —
/// no real waiting, no ffmpeg, no audio files.
/// </summary>
public class MockScenarioTests
{
    private readonly ILogger<MockRadioSource> _logger;
    private readonly Mock<ToneDetector> _toneDetectorMock;
    private readonly Mock<IRecordingService> _recordingServiceMock = new();
    private readonly Mock<IChannelService> _channelServiceMock = new();
    private readonly Mock<IDatabase> _dbMock = new();
    private readonly FakeTimeProvider _time = new();
    private readonly MockRadioSource _source;

    public MockScenarioTests()
    {
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        _logger = loggerFactory.CreateLogger<MockRadioSource>();
        _toneDetectorMock = new Mock<ToneDetector>(_dbMock.Object, new Mock<ILogger<ToneDetector>>().Object);

        // Default channel used by most tests.
        _channelServiceMock.Setup(x => x.Channels).Returns(new List<Channel>
        {
            new(155.000, "Police", "Test Channel", "FM", "FM")
        });

        _source = new MockRadioSource(
            _logger,
            _toneDetectorMock.Object,
            new Mdc1200Decoder(new Mock<ILogger<Mdc1200Decoder>>().Object),
            _dbMock.Object,
            _recordingServiceMock.Object,
            _channelServiceMock.Object,
            new SyntheticAudioProvider(),
            _time);
    }

    // --- Pure event-selection logic (no clock, no threads) ---

    [Fact]
    public void GetActiveEvent_ReturnsEventOnlyWithinItsWindow()
    {
        _source.SetScenario(new List<ScenarioEvent>
        {
            new() { Time = 1, Frequency = 155.0, Duration = 2 },
            new() { Time = 5, Frequency = 156.0, Duration = 1 },
        });

        Assert.Null(_source.GetActiveEvent(0.99));
        Assert.Equal(155.0, _source.GetActiveEvent(1.0)!.Frequency);
        Assert.Equal(155.0, _source.GetActiveEvent(2.99)!.Frequency);
        Assert.Null(_source.GetActiveEvent(3.0));           // end is exclusive
        Assert.Null(_source.GetActiveEvent(4.5));
        Assert.Equal(156.0, _source.GetActiveEvent(5.0)!.Frequency);
        Assert.Null(_source.GetActiveEvent(6.0));
    }

    [Fact]
    public void ScenarioLength_IsEndOfLastEvent()
    {
        _source.SetScenario(new List<ScenarioEvent>
        {
            new() { Time = 1, Frequency = 155.0, Duration = 2 },   // ends at 3
            new() { Time = 5, Frequency = 156.0, Duration = 4 },   // ends at 9
        });

        Assert.Equal(9, _source.ScenarioLength);
    }

    // --- End-to-end simulation, deterministically clocked ---

    [Fact]
    public async Task ScanMode_DetectsSignal_EmitsAudio_ThenResumesScanning()
    {
        _source.SetScenario(new List<ScenarioEvent>
        {
            new() { Time = 1, Frequency = 155.000, Duration = 4, SourceId = 101, TargetId = 202 }
        });

        var audioChunks = 0;
        _source.OnAudio += _ => Interlocked.Increment(ref audioChunks);

        await StartAndSettle();

        // Jump to the event: scanner should lock on and stream its audio.
        await AdvanceAndSettle(TimeSpan.FromSeconds(1));

        var state = _source.GetState();
        Assert.Equal("RECEIVING", state.Status);
        Assert.Equal(155.000, state.CurrentFrequency);
        Assert.Equal(101, state.SourceID);
        Assert.Equal(202, state.TargetID);
        Assert.True(audioChunks > 0, $"expected audio chunks, got {audioChunks}");

        // Jump past the end: scanner should drop the signal and resume scanning.
        await AdvanceAndSettle(TimeSpan.FromSeconds(4));

        Assert.Equal("SCANNING", _source.GetState().Status);
        _recordingServiceMock.Verify(r => r.StopRecording(It.IsAny<Channel>(), It.IsAny<string?>()), Times.Once);

        await _source.StopAsync();
    }

    [Fact]
    public async Task ManualHold_OnDifferentFrequency_DoesNotReceive()
    {
        _source.SetScenario(new List<ScenarioEvent>
        {
            new() { Time = 0.5, Frequency = 155.000, Duration = 2 }
        });
        _channelServiceMock.Setup(x => x.Channels).Returns(new List<Channel>
        {
            new(155.000, "Police", "Test Channel", "FM", "FM"),
            new(156.000, "Marine", "Marine Channel", "FM", "FM")
        });

        var audioChunks = 0;
        _source.OnAudio += _ => Interlocked.Increment(ref audioChunks);

        await StartAndSettle();
        _source.HoldFrequency(156.000); // Hold on a different frequency.

        await AdvanceAndSettle(TimeSpan.FromSeconds(0.5)); // Event fires on 155.

        var state = _source.GetState();
        Assert.Equal("MONITORING", state.Status);
        Assert.Equal(156.000, state.CurrentFrequency);
        Assert.Equal(0, audioChunks);

        await _source.StopAsync();
    }

    [Fact]
    public async Task ManualHold_OnMatchingFrequency_ReceivesAndBuffersPreRoll()
    {
        _source.SetScenario(new List<ScenarioEvent>
        {
            new() { Time = 0.5, Frequency = 155.000, Duration = 4.8, SourceId = 999, TargetId = 888 }
        });

        var audioChunks = 0;
        _source.OnAudio += _ => Interlocked.Increment(ref audioChunks);

        await StartAndSettle();
        _source.HoldFrequency(155.000);

        await AdvanceAndSettle(TimeSpan.FromSeconds(0.5));

        Assert.True(audioChunks > 10, $"expected steady audio, got {audioChunks}");
        Assert.Equal("MONITORING", _source.GetState().Status);
        Assert.NotEmpty(_source.GetPreRollBuffer());

        await _source.StopAsync();
    }

    [Fact]
    public async Task ScanMode_AvoidedFrequency_IsSkipped()
    {
        _source.SetScenario(new List<ScenarioEvent>
        {
            new() { Time = 1, Frequency = 155.000, Duration = 3 }
        });

        var audioChunks = 0;
        _source.OnAudio += _ => Interlocked.Increment(ref audioChunks);

        await StartAndSettle();
        _source.AvoidFrequency(155.000, 10); // Avoid it for the whole event.

        await AdvanceAndSettle(TimeSpan.FromSeconds(1));

        Assert.Equal("SCANNING", _source.GetState().Status);
        Assert.Equal(0, audioChunks);

        await _source.StopAsync();
    }

    // --- Edge cases ---

    [Fact]
    public void GetActiveEvent_OverlappingEvents_ReturnsFirstMatch()
    {
        _source.SetScenario(new List<ScenarioEvent>
        {
            new() { Time = 1, Frequency = 155.0, Duration = 5 },
            new() { Time = 2, Frequency = 156.0, Duration = 5 }, // overlaps the first
        });

        Assert.Equal(155.0, _source.GetActiveEvent(3.0)!.Frequency);
    }

    [Fact]
    public void ScenarioLength_EmptyScenario_IsZero()
    {
        _source.SetScenario(new List<ScenarioEvent>());
        Assert.Equal(0, _source.ScenarioLength);
        Assert.Null(_source.GetActiveEvent(0));
    }

    [Fact]
    public void IsAvoided_IsTrueWithinWindow_AndExpiresAfterDuration()
    {
        _source.AvoidFrequency(155.0, 2);

        Assert.True(_source.IsAvoided(155.0));
        Assert.False(_source.IsAvoided(156.0)); // only the avoided frequency

        _time.Advance(TimeSpan.FromSeconds(2.5));
        Assert.False(_source.IsAvoided(155.0)); // window expired
    }

    [Fact]
    public async Task ScanMode_AfterAvoidExpires_ReceivesAgain()
    {
        _source.SetScenario(new List<ScenarioEvent>
        {
            new() { Time = 5, Frequency = 155.000, Duration = 3 }
        });

        await StartAndSettle();
        _source.AvoidFrequency(155.000, 2); // expires well before the event at t=5

        await AdvanceAndSettle(TimeSpan.FromSeconds(5));

        Assert.Equal("RECEIVING", _source.GetState().Status);

        await _source.StopAsync();
    }

    [Fact]
    public async Task ManualHold_AvoidIsIgnored_StillReceives()
    {
        _source.SetScenario(new List<ScenarioEvent>
        {
            new() { Time = 1, Frequency = 155.000, Duration = 3 }
        });

        var audioChunks = 0;
        _source.OnAudio += _ => Interlocked.Increment(ref audioChunks);

        await StartAndSettle();
        _source.HoldFrequency(155.000);
        _source.AvoidFrequency(155.000, 100); // avoid is a scan-mode concept only

        await AdvanceAndSettle(TimeSpan.FromSeconds(1));

        Assert.Equal("MONITORING", _source.GetState().Status);
        Assert.True(audioChunks > 0, "manual hold should still receive an avoided frequency");

        await _source.StopAsync();
    }

    // --- Helpers: drive the loop deterministically via the fake clock ---

    private async Task StartAndSettle()
    {
        _source.Start();
        await WaitFor(() => _source.ParkCount >= 1);
    }

    private async Task AdvanceAndSettle(TimeSpan span)
    {
        var before = _source.ParkCount;
        _time.Advance(span);
        await WaitFor(() => _source.ParkCount > before);
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(2);
        Assert.True(condition(), "loop did not reach the expected state in time");
    }
}
