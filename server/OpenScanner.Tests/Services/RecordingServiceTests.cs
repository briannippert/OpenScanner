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
