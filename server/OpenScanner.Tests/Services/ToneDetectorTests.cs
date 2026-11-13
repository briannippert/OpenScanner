using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using OpenScanner.Server;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using OpenScanner.Server.Interfaces;
using Xunit;

namespace OpenScanner.Tests;

public class ToneDetectorTests
{
    private readonly Mock<IDatabase> _dbMock = new();
    private readonly Mock<ILogger<ToneDetector>> _loggerMock = new();

    // Tone timing runs on a fake clock so results don't depend on how busy the machine is.
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task Detects_TwoTone_Sequence()
    {
        // Arrange
        var toneSet = new FireToneSet { Name = "Test Station", FrequencyA = 600, FrequencyB = 800 };
        _dbMock.Setup(db => db.GetAllFireTonesAsync()).ReturnsAsync(new[] { toneSet });

        var detector = new ToneDetector(_dbMock.Object, _loggerMock.Object, _time);
        // Wait for async reload — poll to avoid a fixed delay that can be too short in CI
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (detector.ToneCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        FireToneSet? detected = null;
        detector.OnToneDetected += (t) => detected = t;

        // Generate Audio
        var sampleRate = 48000;
        var toneA = GenerateSineWave(600, 1.0, sampleRate); // 1s of 600Hz
        var toneB = GenerateSineWave(800, 1.0, sampleRate); // 1s of 800Hz
        
        // Act
        detector.ProcessAudio(toneA); // Should detect Tone A
        detector.ProcessAudio(toneB); // Should detect Tone B and fire event

        // Assert
        Assert.NotNull(detected);
        Assert.Equal("Test Station", detected.Name);
    }

    [Fact]
    public async Task Ignores_Noise()
    {
        // Arrange
        var toneSet = new FireToneSet { Name = "Test Station", FrequencyA = 600, FrequencyB = 800 };
        _dbMock.Setup(db => db.GetAllFireTonesAsync()).ReturnsAsync(new[] { toneSet });

        var detector = new ToneDetector(_dbMock.Object, _loggerMock.Object, _time);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (detector.ToneCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        bool fired = false;
        detector.OnToneDetected += (_) => fired = true;

        var noise = GenerateSineWave(1000, 1.0, 48000); // Wrong frequency

        // Act
        detector.ProcessAudio(noise);

        // Assert
        Assert.False(fired);
    }

    [Fact]
    public async Task Resets_If_ToneB_Too_Late()
    {
        // Arrange
        var toneSet = new FireToneSet { Name = "Test Station", FrequencyA = 600, FrequencyB = 800 };
        _dbMock.Setup(db => db.GetAllFireTonesAsync()).ReturnsAsync(new[] { toneSet });

        var detector = new ToneDetector(_dbMock.Object, _loggerMock.Object, _time);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (detector.ToneCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        bool fired = false;
        detector.OnToneDetected += (_) => fired = true;

        var toneA = GenerateSineWave(600, 1.0, 48000);
        var toneB = GenerateSineWave(800, 1.0, 48000);
        var silence = new byte[48000 * 2 * 4]; // 4 seconds of silence

        // Act
        detector.ProcessAudio(toneA); // Tone A detected
        _time.Advance(TimeSpan.FromSeconds(3.1));
        detector.ProcessAudio(toneB); // Should be ignored as A expired

        // Assert
        Assert.False(fired);
    }

    [Fact]
    public async Task Detects_SingleTone_WhenSustained()
    {
        // A tone set with no Tone B is a single (long) tone page.
        var toneSet = new FireToneSet { Name = "Single Station", FrequencyA = 1010, FrequencyB = 0 };
        _dbMock.Setup(db => db.GetAllFireTonesAsync()).ReturnsAsync(new[] { toneSet });

        var detector = new ToneDetector(_dbMock.Object, _loggerMock.Object, _time);
        var reload = DateTime.UtcNow.AddSeconds(2);
        while (detector.ToneCount == 0 && DateTime.UtcNow < reload)
            await Task.Delay(10);

        int fireCount = 0;
        FireToneSet? detected = null;
        detector.OnToneDetected += (t) => { detected = t; fireCount++; };

        // Feed 100ms tone chunks continuously for 1.6s, mimicking live audio arrival.
        var chunk = GenerateSineWave(1010, 0.1, 48000);
        for (int i = 0; i < 16; i++)
        {
            detector.ProcessAudio(chunk);
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.NotNull(detected);
        Assert.Equal("Single Station", detected!.Name);
        // A single sustained page must fire exactly once, not repeatedly per chunk.
        Assert.Equal(1, fireCount);
    }

    [Fact]
    public async Task SingleTone_TooShort_DoesNotFire()
    {
        var toneSet = new FireToneSet { Name = "Single Station", FrequencyA = 1010, FrequencyB = 0 };
        _dbMock.Setup(db => db.GetAllFireTonesAsync()).ReturnsAsync(new[] { toneSet });

        var detector = new ToneDetector(_dbMock.Object, _loggerMock.Object, _time);
        var reload = DateTime.UtcNow.AddSeconds(2);
        while (detector.ToneCount == 0 && DateTime.UtcNow < reload)
            await Task.Delay(10);

        bool fired = false;
        detector.OnToneDetected += (_) => fired = true;

        // A brief burst (well under the 1s minimum) must not fire.
        detector.ProcessAudio(GenerateSineWave(1010, 0.1, 48000));

        Assert.False(fired);
    }

    private async Task<ToneDetector> MakeTwoToneDetector()
    {
        var toneSet = new FireToneSet { Name = "Test Station", FrequencyA = 600, FrequencyB = 800 };
        _dbMock.Setup(db => db.GetAllFireTonesAsync()).ReturnsAsync(new[] { toneSet });
        var detector = new ToneDetector(_dbMock.Object, _loggerMock.Object, _time);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (detector.ToneCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        return detector;
    }

    [Fact]
    public async Task KeyedStreams_DoNotMixToneAAndToneBFromDifferentChannels()
    {
        var detector = await MakeTwoToneDetector();
        var fired = new List<double>();
        detector.OnStreamToneDetected += (_, key) => fired.Add(key);

        // Channel 1 carries tone A and channel 2 tone B, interleaved as parallel scanning
        // delivers them. Neither channel heard a complete A-then-B page.
        detector.ProcessAudio(GenerateSineWave(600, 1.0, 48000), 460.0);
        detector.ProcessAudio(GenerateSineWave(800, 1.0, 48000), 461.0);

        Assert.Empty(fired);
    }

    [Fact]
    public async Task KeyedStreams_DetectToneSequenceOnSingleChannelAndReportItsKey()
    {
        var detector = await MakeTwoToneDetector();
        var fired = new List<double>();
        detector.OnStreamToneDetected += (_, key) => fired.Add(key);
        FireToneSet? unkeyed = null;
        detector.OnToneDetected += t => unkeyed = t;

        detector.ProcessAudio(GenerateSineWave(600, 1.0, 48000), 460.0);
        // Another channel's audio in between must not disturb channel 460's state.
        detector.ProcessAudio(GenerateSineWave(1000, 1.0, 48000), 461.0);
        detector.ProcessAudio(GenerateSineWave(800, 1.0, 48000), 460.0);

        Assert.Equal(new[] { 460.0 }, fired);
        Assert.Null(unkeyed); // keyed streams don't use the single-channel event
    }

    /// <summary>Feeds audio in chunks of <paramref name="chunkSamples"/>, advancing the clock to match.</summary>
    private void Feed(ToneDetector detector, byte[] pcm, int chunkSamples, double? key = null)
    {
        var bytes = chunkSamples * 2;
        for (var off = 0; off < pcm.Length; off += bytes)
        {
            var chunk = pcm[off..Math.Min(pcm.Length, off + bytes)];
            if (key is { } k) detector.ProcessAudio(chunk, k); else detector.ProcessAudio(chunk);
            _time.Advance(TimeSpan.FromSeconds(chunk.Length / 2 / 48000.0));
        }
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Theory]
    [InlineData(800)]
    [InlineData(1300)] // parallel-pipeline chunk size
    [InlineData(1600)]
    [InlineData(4800)]
    [InlineData(48000)]
    public async Task TwoTonePage_FiresOnce_WhateverTheChunkSize(int chunkSamples)
    {
        var detector = await MakeTwoToneDetector();
        var count = 0;
        detector.OnToneDetected += _ => count++;

        Feed(detector, Concat(GenerateSineWave(600, 1.0, 48000), GenerateSineWave(800, 3.0, 48000)), chunkSamples);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task QuietPage_IsDetected()
    {
        var detector = await MakeTwoToneDetector();
        var fired = false;
        detector.OnToneDetected += _ => fired = true;

        // About -34 dBFS: purity, not loudness, decides whether a tone is present.
        Feed(detector, Concat(GenerateSineWave(600, 1.0, 48000, 0.02), GenerateSineWave(800, 3.0, 48000, 0.02)), 1300);

        Assert.True(fired);
    }

    [Fact]
    public async Task PageUnderNoise_IsDetected()
    {
        var detector = await MakeTwoToneDetector();
        var fired = false;
        detector.OnToneDetected += _ => fired = true;

        var rng = new Random(1);
        byte[] Noisy(byte[] tone)
        {
            var copy = (byte[])tone.Clone();
            for (var i = 0; i < copy.Length; i += 2)
            {
                var v = BitConverter.ToInt16(copy, i) + (rng.NextDouble() * 2 - 1) * 0.15 * 32767;
                BitConverter.TryWriteBytes(new Span<byte>(copy, i, 2), (short)Math.Clamp(v, short.MinValue, short.MaxValue));
            }
            return copy;
        }

        Feed(detector, Concat(Noisy(GenerateSineWave(600, 1.0, 48000, 0.5)), Noisy(GenerateSineWave(800, 3.0, 48000, 0.5))), 1300);

        Assert.True(fired);
    }

    [Fact]
    public async Task BriefTonesLikeSpeech_DoNotFire()
    {
        var detector = await MakeTwoToneDetector();
        var fired = false;
        detector.OnToneDetected += _ => fired = true;

        // Speech touches a tone frequency for a moment but never holds it: alternate short
        // bursts of A and B with other pitches in between, as the voice after a page does.
        var parts = new List<byte[]>();
        for (var i = 0; i < 20; i++)
        {
            parts.Add(GenerateSineWave(600, 0.2, 48000));
            parts.Add(GenerateSineWave(450, 0.1, 48000));
            parts.Add(GenerateSineWave(800, 0.3, 48000));
            parts.Add(GenerateSineWave(700, 0.1, 48000));
        }
        Feed(detector, Concat(parts.ToArray()), 1300);

        Assert.False(fired);
    }

    [Fact]
    public async Task ShortToneA_DoesNotFire()
    {
        var detector = await MakeTwoToneDetector();
        var fired = false;
        detector.OnToneDetected += _ => fired = true;

        Feed(detector, Concat(GenerateSineWave(600, 0.3, 48000), GenerateSineWave(800, 3.0, 48000)), 1300);

        Assert.False(fired);
    }

    [Fact]
    public async Task NeighbouringQuickCallTone_DoesNotFire()
    {
        // 617.4 Hz is the next Quick Call II tone above 600-ish; it must not pass for 600.
        var detector = await MakeTwoToneDetector();
        var fired = false;
        detector.OnToneDetected += _ => fired = true;

        Feed(detector, Concat(GenerateSineWave(617.4, 1.0, 48000), GenerateSineWave(800, 3.0, 48000)), 1300);

        Assert.False(fired);
    }

    [Fact]
    public async Task ToneBAfterAPause_DoesNotPairWithOldToneA()
    {
        var detector = await MakeTwoToneDetector();
        var fired = false;
        detector.OnToneDetected += _ => fired = true;

        // Silence (the carrier still up) longer than the allowed gap between A and B.
        Feed(detector, Concat(GenerateSineWave(600, 1.0, 48000), new byte[48000 * 2 * 2], GenerateSineWave(800, 3.0, 48000)), 1300);

        Assert.False(fired);
    }

    // Real recordings, each with one page followed by dispatch voice. Every recording is run
    // against every tone set (including each two-tone set reversed): it must fire its own
    // page exactly once and nothing else. The old detector fired repeatedly during the voice.
    private static readonly FireToneSet[] RecordedToneSets =
    {
        new() { Name = "567/376", FrequencyA = 567.4, FrequencyB = 376.5 },
        new() { Name = "376/567 reversed", FrequencyA = 376.5, FrequencyB = 567.4 },
        new() { Name = "790/719", FrequencyA = 790.0, FrequencyB = 719.4 },
        new() { Name = "719/790 reversed", FrequencyA = 719.4, FrequencyB = 790.0 },
        new() { Name = "1010 single", FrequencyA = 1010.6, FrequencyB = 0 },
        new() { Name = "700/900 unrelated", FrequencyA = 700, FrequencyB = 900 },
    };

    [Theory]
    [InlineData("fto_567_376_with_voice.mp3", "567/376")]   // 566 Hz 1 s, 376 Hz 3 s
    [InlineData("fto_790_719_with_voice.mp3", "790/719")]   // 790 Hz 1 s, 719 Hz 3 s
    [InlineData("fto_single_1010_a.mp3", "1010 single")]    // 1010.6 Hz 1.5 s, from the start
    [InlineData("fto_single_1010_b.mp3", "1010 single")]    // 1010.6 Hz 2.45 s, mid-recording
    public async Task RealRecording_FiresItsOwnPageOnce_AndNothingElse(string file, string expected)
    {
        var raw = DecodeWithFfmpeg(Path.Combine(AppContext.BaseDirectory, "TestData", file));
        _dbMock.Setup(db => db.GetAllFireTonesAsync()).ReturnsAsync(RecordedToneSets);
        var detector = new ToneDetector(_dbMock.Object, _loggerMock.Object, _time);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (detector.ToneCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);

        foreach (var chunk in new[] { 800, 1300, 1600, 4800 })
        {
            detector.ResetStreams();
            var hits = new List<string>();
            void Record(FireToneSet t, double _) => hits.Add(t.Name);
            detector.OnStreamToneDetected += Record;
            Feed(detector, raw, chunk, 154.175);
            detector.OnStreamToneDetected -= Record;
            Assert.True(hits.SequenceEqual(new[] { expected }), $"chunk {chunk}: [{string.Join(", ", hits)}]");
        }
    }

    [Theory]
    [InlineData(788.5, 716.1)] // the Quick Call II chart values nearest the measured 790 / 719.4
    [InlineData(791.5, 721.0)]
    public async Task RealRecording_ToleratesSmallTransmitterOffsets(double a, double b)
    {
        var raw = DecodeWithFfmpeg(Path.Combine(AppContext.BaseDirectory, "TestData", "fto_790_719_with_voice.mp3"));
        _dbMock.Setup(db => db.GetAllFireTonesAsync()).ReturnsAsync(new[] { new FireToneSet { Name = "chart", FrequencyA = a, FrequencyB = b } });
        var detector = new ToneDetector(_dbMock.Object, _loggerMock.Object, _time);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (detector.ToneCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        var count = 0;
        detector.OnToneDetected += _ => count++;

        Feed(detector, raw, 1300);

        Assert.Equal(1, count);
    }

    private static byte[] DecodeWithFfmpeg(string path)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg", $"-v error -i \"{path}\" -ac 1 -ar 48000 -f s16le -")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        using var ms = new MemoryStream();
        var stderr = proc.StandardError.ReadToEndAsync();
        proc.StandardOutput.BaseStream.CopyTo(ms);
        proc.WaitForExit();
        Assert.True(proc.ExitCode == 0, "ffmpeg failed: " + stderr.Result);
        return ms.ToArray();
    }

    private byte[] GenerateSineWave(double freq, double durationSec, int sampleRate, double level = 0.8)
    {
        int samples = (int)(sampleRate * durationSec);
        byte[] buffer = new byte[samples * 2]; // 16-bit
        double amplitude = level * 32767;

        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / sampleRate;
            short s = (short)(amplitude * Math.Sin(2 * Math.PI * freq * t));
            BitConverter.TryWriteBytes(new Span<byte>(buffer, i * 2, 2), s);
        }
        return buffer;
    }
}
