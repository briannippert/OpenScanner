using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Devices;

/// <summary>
/// Produces deterministic synthetic PCM audio for a scenario event with no file
/// I/O and no external processes, making it safe and repeatable for tests. The
/// chunk count is a pure function of <see cref="ScenarioEvent.Duration"/>, and
/// all chunks are emitted synchronously so tests can assert on them immediately
/// after an event is detected (no clock advancing required to pump audio).
/// </summary>
public class SyntheticAudioProvider : IMockAudioProvider
{
    private const int SampleRate = 48000;
    private const int ChunkBytes = 3200;                       // ~33 ms, s16le mono
    private const int SamplesPerChunk = ChunkBytes / 2;
    private const double ChunkSeconds = SamplesPerChunk / (double)SampleRate;
    private const double ToneHz = 440.0;
    private const short Amplitude = 8000;

    public Task StreamAsync(ScenarioEvent evt, Action<byte[]> onChunk, CancellationToken token)
    {
        var duration = evt.Duration > 0 ? evt.Duration : ChunkSeconds;
        var chunkCount = Math.Max(1, (int)Math.Round(duration / ChunkSeconds));

        double phase = 0;
        double phaseStep = 2 * Math.PI * ToneHz / SampleRate;

        for (var i = 0; i < chunkCount; i++)
        {
            token.ThrowIfCancellationRequested();

            var chunk = new byte[ChunkBytes];
            for (var s = 0; s < SamplesPerChunk; s++)
            {
                var sample = (short)(Math.Sin(phase) * Amplitude);
                phase += phaseStep;
                chunk[s * 2] = (byte)(sample & 0xFF);
                chunk[s * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            onChunk(chunk);
        }

        return Task.CompletedTask;
    }
}
