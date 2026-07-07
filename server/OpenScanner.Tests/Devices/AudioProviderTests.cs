using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using OpenScanner.Server.Devices;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using Xunit;

namespace OpenScanner.Tests;

public class SyntheticAudioProviderTests
{
    private readonly SyntheticAudioProvider _provider = new();

    // ChunkSeconds = (3200 bytes / 2 bytes-per-sample) / 48000 Hz = 1/30 s.
    // So the deterministic chunk count is round(duration * 30).
    [Theory]
    [InlineData(1.0, 30)]
    [InlineData(2.0, 60)]
    [InlineData(4.8, 144)]
    [InlineData(0.0, 1)]   // zero/negative duration still yields one chunk
    public async Task StreamAsync_EmitsDeterministicChunkCount(double duration, int expected)
    {
        var chunks = 0;
        await _provider.StreamAsync(
            new ScenarioEvent { Duration = duration, Frequency = 155.0 },
            _ => chunks++,
            CancellationToken.None);

        Assert.Equal(expected, chunks);
    }

    [Fact]
    public async Task StreamAsync_ProducesFullSizeChunks()
    {
        var sizes = new List<int>();
        await _provider.StreamAsync(
            new ScenarioEvent { Duration = 1.0 },
            c => sizes.Add(c.Length),
            CancellationToken.None);

        Assert.NotEmpty(sizes);
        Assert.All(sizes, s => Assert.Equal(3200, s));
    }

    [Fact]
    public async Task StreamAsync_IsBytewiseDeterministic()
    {
        var first = new List<byte[]>();
        var second = new List<byte[]>();
        var evt = new ScenarioEvent { Duration = 0.5 };

        await _provider.StreamAsync(evt, first.Add, CancellationToken.None);
        await _provider.StreamAsync(evt, second.Add, CancellationToken.None);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
            Assert.Equal(first[i], second[i]);
    }

    [Fact]
    public async Task StreamAsync_CancelledToken_ThrowsAndEmitsNothing()
    {
        var chunks = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _provider.StreamAsync(new ScenarioEvent { Duration = 2.0 }, _ => chunks++, cts.Token));

        Assert.Equal(0, chunks);
    }
}

public class FileAudioProviderTests
{
    private readonly FileAudioProvider _provider;
    private readonly FakeTimeProvider _time = new();

    public FileAudioProviderTests()
    {
        var logger = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Critical))
            .CreateLogger<FileAudioProvider>();
        _provider = new FileAudioProvider(logger, new Mock<IDecoderFactory>().Object, _time);
    }

    [Fact]
    public async Task StreamAsync_NoAudioFile_EmitsNothing()
    {
        var chunks = 0;
        await _provider.StreamAsync(
            new ScenarioEvent { AudioFile = null, Frequency = 155.0 },
            _ => chunks++,
            CancellationToken.None);

        Assert.Equal(0, chunks);
    }

    [Fact]
    public async Task StreamAsync_MissingFile_EmitsNothingAndDoesNotThrow()
    {
        var chunks = 0;
        await _provider.StreamAsync(
            new ScenarioEvent { AudioFile = "definitely_not_here.wav", Frequency = 155.0, DecoderType = "FM" },
            _ => chunks++,
            CancellationToken.None);

        Assert.Equal(0, chunks);
    }

    [Fact]
    public async Task StreamAsync_RawWav_StreamsOneChunkPerFrame()
    {
        // 3 full 3200-byte frames after the 44-byte WAV header.
        const int frames = 3;
        var testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        Directory.CreateDirectory(testDataDir);
        var name = $"synthetic_{Guid.NewGuid():N}.wav";
        var path = Path.Combine(testDataDir, name);
        await File.WriteAllBytesAsync(path, new byte[44 + frames * 3200]);

        try
        {
            var chunks = 0;
            var evt = new ScenarioEvent { AudioFile = name, Frequency = 155.0, DecoderType = "FM" };
            var task = _provider.StreamAsync(evt, _ => Interlocked.Increment(ref chunks), CancellationToken.None);

            // Pump the fake clock so the per-frame throttle delays release.
            var sw = Stopwatch.StartNew();
            while (!task.IsCompleted && sw.ElapsedMilliseconds < 2000)
            {
                _time.Advance(TimeSpan.FromMilliseconds(33));
                await Task.Delay(2);
            }
            await task;

            Assert.Equal(frames, chunks);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
