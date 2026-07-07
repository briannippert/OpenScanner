using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task Detects_TwoTone_Sequence()
    {
        // Arrange
        var toneSet = new FireToneSet { Name = "Test Station", FrequencyA = 600, FrequencyB = 800 };
        _dbMock.Setup(db => db.GetAllFireTonesAsync()).ReturnsAsync(new[] { toneSet });

        var detector = new ToneDetector(_dbMock.Object, _loggerMock.Object);
        // Wait for async reload — poll to avoid a fixed delay that can be too short in CI
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (detector.ToneCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        FireToneSet? detected = null;
        detector.OnToneDetected += (t) => detected = t;

        // Generate Audio
        var sampleRate = 48000;
        var toneA = GenerateSineWave(600, 0.5, sampleRate); // 0.5s of 600Hz
        var toneB = GenerateSineWave(800, 0.5, sampleRate); // 0.5s of 800Hz
        
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

        var detector = new ToneDetector(_dbMock.Object, _loggerMock.Object);
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

        var detector = new ToneDetector(_dbMock.Object, _loggerMock.Object);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (detector.ToneCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        bool fired = false;
        detector.OnToneDetected += (_) => fired = true;

        var toneA = GenerateSineWave(600, 0.5, 48000);
        var toneB = GenerateSineWave(800, 0.5, 48000);
        var silence = new byte[48000 * 2 * 4]; // 4 seconds of silence

        // Act
        detector.ProcessAudio(toneA); // Tone A detected
        await Task.Delay(3100); 
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

        var detector = new ToneDetector(_dbMock.Object, _loggerMock.Object);
        var reload = DateTime.UtcNow.AddSeconds(2);
        while (detector.ToneCount == 0 && DateTime.UtcNow < reload)
            await Task.Delay(10);

        int fireCount = 0;
        FireToneSet? detected = null;
        detector.OnToneDetected += (t) => { detected = t; fireCount++; };

        // Feed 100ms tone chunks continuously for ~1.5s, mimicking live audio arrival.
        var chunk = GenerateSineWave(1010, 0.1, 48000);
        var deadline = DateTime.UtcNow.AddSeconds(1.6);
        while (DateTime.UtcNow < deadline)
        {
            detector.ProcessAudio(chunk);
            await Task.Delay(80);
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

        var detector = new ToneDetector(_dbMock.Object, _loggerMock.Object);
        var reload = DateTime.UtcNow.AddSeconds(2);
        while (detector.ToneCount == 0 && DateTime.UtcNow < reload)
            await Task.Delay(10);

        bool fired = false;
        detector.OnToneDetected += (_) => fired = true;

        // A brief burst (well under the 1s minimum) must not fire.
        detector.ProcessAudio(GenerateSineWave(1010, 0.1, 48000));

        Assert.False(fired);
    }

    private byte[] GenerateSineWave(double freq, double durationSec, int sampleRate)
    {
        int samples = (int)(sampleRate * durationSec);
        byte[] buffer = new byte[samples * 2]; // 16-bit
        double amplitude = 0.8 * 32767;

        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / sampleRate;
            short s = (short)(amplitude * Math.Sin(2 * Math.PI * freq * t));
            BitConverter.TryWriteBytes(new Span<byte>(buffer, i * 2, 2), s);
        }
        return buffer;
    }
}
