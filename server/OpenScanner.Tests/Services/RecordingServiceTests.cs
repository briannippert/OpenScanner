using Microsoft.Extensions.Logging;
using Moq;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests;

public class RecordingServiceTests
{
    private readonly Mock<IDatabase> _db = new();
    private readonly Mock<ITranscriptionService> _transcription = new();
    private readonly RecordingService _service;

    public RecordingServiceTests()
    {
        var lf = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Critical));
        var gps = new GpsService(lf.CreateLogger<GpsService>());
        _service = new RecordingService(
            _db.Object, lf.CreateLogger<RecordingService>(), _transcription.Object, gps);
    }

    private static Channel Chan(double freq = 155.0) => new(freq, "Test", "Test channel");

    [Fact]
    public void IsRecording_IsFalseInitially()
    {
        Assert.False(_service.IsRecording);
    }

    [Fact]
    public void StartRecording_SetsIsRecording()
    {
        _service.StartRecording(Chan(), 1, 2, new LinkedList<byte[]>());
        try
        {
            Assert.True(_service.IsRecording);
        }
        finally
        {
            _service.StopRecording(Chan(), null); // short -> aborts and cleans up file
        }
    }

    [Fact]
    public void StartRecording_WhenAlreadyRecording_IsIgnored()
    {
        _service.StartRecording(Chan(), 1, 2, new LinkedList<byte[]>());
        try
        {
            // Second start must not replace the active stream.
            _service.StartRecording(Chan(999.0), 3, 4, new LinkedList<byte[]>());
            Assert.True(_service.IsRecording);
        }
        finally
        {
            _service.StopRecording(Chan(), null);
        }
    }

    [Fact]
    public void StopRecording_WhenNotRecording_DoesNothing()
    {
        _service.StopRecording(Chan(), null); // should be a no-op, no throw

        Assert.False(_service.IsRecording);
        _db.Verify(d => d.SaveTransmissionAsync(It.IsAny<CallLog>()), Times.Never);
    }

    [Fact]
    public void StopRecording_ShortRecording_AbortsWithoutSavingOrLogging()
    {
        var newLogs = 0;
        _service.OnNewLog += _ => newLogs++;

        _service.StartRecording(Chan(), 1, 2, new LinkedList<byte[]>());
        _service.StopRecording(Chan(), null); // ~0s duration -> aborted

        Assert.False(_service.IsRecording);
        Assert.Equal(0, newLogs);
        _db.Verify(d => d.SaveTransmissionAsync(It.IsAny<CallLog>()), Times.Never);
        _transcription.Verify(t => t.QueueTranscription(It.IsAny<CallLog>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ProcessAudio_WhenNotRecording_IsSafeNoOp()
    {
        // Neither legacy nor parallel forms should throw when nothing is recording.
        _service.ProcessAudio(new byte[3200]);
        _service.ProcessAudio(155.0, new byte[3200]);

        Assert.False(_service.IsRecording);
    }

    [Fact]
    public void StopParallelRecording_UnknownFrequency_DoesNothing()
    {
        _service.StopParallelRecording(999.0, null); // no throw

        _db.Verify(d => d.SaveTransmissionAsync(It.IsAny<CallLog>()), Times.Never);
    }

    [Fact]
    public void IsChannelRecording_TracksParallelRecordings()
    {
        Assert.False(_service.IsChannelRecording(155.0));

        _service.StartParallelRecording(Chan(155.0), 1, 2, new LinkedList<byte[]>());
        try
        {
            Assert.True(_service.IsChannelRecording(155.0));
            Assert.False(_service.IsChannelRecording(156.0));
        }
        finally
        {
            _service.StopParallelRecording(155.0, null); // short -> aborts
        }

        Assert.False(_service.IsChannelRecording(155.0));
    }

    [Fact]
    public void CurrentRecordingId_IsNullWhenIdle()
    {
        Assert.Null(_service.CurrentRecordingId);
    }

    [Fact]
    public void CurrentRecordingId_LegacyRecording_MatchesFinalizedLogId()
    {
        CallLog? saved = null;
        _db.Setup(d => d.SaveTransmissionAsync(It.IsAny<CallLog>()))
           .Callback<CallLog>(l => saved = l)
           .Returns(Task.CompletedTask);

        _service.StartRecording(Chan(), 1, 2, new LinkedList<byte[]>());

        // The id observed mid-recording (used to link coincident events) must equal
        // the id of the finalized CallLog for a long-enough recording.
        var liveId = _service.CurrentRecordingId;
        Assert.NotNull(liveId);
        Assert.StartsWith("log_", liveId);

        // Feed enough audio to clear the 4KB minimum, then wait out the 0.5s floor.
        _service.ProcessAudio(new byte[8192]);
        Thread.Sleep(600);
        _service.StopRecording(Chan(), "Station 1");

        Assert.NotNull(saved);
        Assert.Equal(liveId, saved!.Id);
        Assert.Null(_service.CurrentRecordingId);
    }

    [Fact]
    public void CurrentRecordingId_ParallelRecording_ReturnsMostRecentActive()
    {
        _service.StartParallelRecording(Chan(155.0), 1, 2, new LinkedList<byte[]>());
        try
        {
            var id = _service.CurrentRecordingId;
            Assert.NotNull(id);
            Assert.StartsWith("log_", id);
        }
        finally
        {
            _service.StopParallelRecording(155.0, null); // short -> aborts
        }

        Assert.Null(_service.CurrentRecordingId);
    }

    [Fact]
    public void ParallelRecording_AppendSpeaker_BuildsOrderedSpeakerChain()
    {
        CallLog? saved = null;
        _db.Setup(d => d.SaveTransmissionAsync(It.IsAny<CallLog>()))
           .Callback<CallLog>(l => saved = l)
           .Returns(Task.CompletedTask);

        // First talker seeded by StartParallelRecording; subsequent talkers appended.
        _service.StartParallelRecording(Chan(155.0), 1, 2, new LinkedList<byte[]>());
        _service.AppendSpeaker(155.0, 2);
        _service.AppendSpeaker(155.0, 2); // consecutive repeat -> de-duped
        _service.AppendSpeaker(155.0, 1);

        // Feed enough audio to clear the 4KB minimum, then wait out the 0.5s floor.
        _service.ProcessAudio(155.0, new byte[8192]);
        Thread.Sleep(600);
        _service.StopParallelRecording(155.0, null);

        Assert.NotNull(saved);
        Assert.Equal("1 → 2 → 1", saved!.SpeakerChain);
    }

    [Fact]
    public void StartParallelRecording_SameFrequencyTwice_IsIgnored()
    {
        _service.StartParallelRecording(Chan(155.0), 1, 2, new LinkedList<byte[]>());
        try
        {
            _service.StartParallelRecording(Chan(155.0), 3, 4, new LinkedList<byte[]>());
            Assert.True(_service.IsChannelRecording(155.0));
        }
        finally
        {
            _service.StopParallelRecording(155.0, null);
        }
    }
}
