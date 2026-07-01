using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Devices;

/// <summary>
/// Streams scenario audio from real recorded files in <c>TestData/</c>. P25
/// events are piped through the real P25 decoder (via ffmpeg at native rate);
/// other modes stream the WAV payload directly, throttled to real time. This is
/// the provider used when the app runs with <c>Radio:Provider=Mock</c> for a
/// realistic hands-on demo without hardware.
/// </summary>
public class FileAudioProvider : IMockAudioProvider
{
    // ~33 ms of 48 kHz s16le mono audio.
    private const int ChunkBytes = 3200;
    private static readonly TimeSpan ChunkInterval = TimeSpan.FromMilliseconds(33);

    private readonly ILogger<FileAudioProvider> _logger;
    private readonly IDecoderFactory _decoderFactory;
    private readonly TimeProvider _timeProvider;

    public FileAudioProvider(
        ILogger<FileAudioProvider> logger,
        IDecoderFactory decoderFactory,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _decoderFactory = decoderFactory;
        _timeProvider = timeProvider;
    }

    public async Task StreamAsync(ScenarioEvent evt, Action<byte[]> onChunk, CancellationToken token)
    {
        if (string.IsNullOrEmpty(evt.AudioFile))
        {
            _logger.LogWarning(
                "[FileAudioProvider] Event at {Frequency} MHz has no AudioFile; nothing to play.",
                evt.Frequency);
            return;
        }

        // Files are copied next to the assembly via CopyToOutputDirectory, so a
        // single deterministic resolution is enough (no working-dir guessing).
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", evt.AudioFile);
        if (!File.Exists(path))
        {
            _logger.LogError("[FileAudioProvider] Audio file not found: {Path}", path);
            return;
        }

        if (token.IsCancellationRequested) return;

        _logger.LogInformation(
            "[FileAudioProvider] Playing {Path} (decoder: {Decoder})",
            path, evt.DecoderType ?? "none");

        if (string.Equals(evt.DecoderType, "P25", StringComparison.OrdinalIgnoreCase))
        {
            await StreamViaDecoderAsync(evt, path, onChunk, token);
        }
        else
        {
            await StreamRawAsync(path, onChunk, token);
        }
    }

    private async Task StreamViaDecoderAsync(
        ScenarioEvent evt, string path, Action<byte[]> onChunk, CancellationToken token)
    {
        IDecoder decoder;
        try
        {
            decoder = _decoderFactory.GetDecoder("P25");
        }
        catch (ObjectDisposedException)
        {
            return; // Shutting down.
        }

        // -re makes ffmpeg read at native rate, so the decoder runs in real time
        // instead of flooding the client.
        decoder.InputSource =
            $"{PlatformTools.Ffmpeg} -re -i \"{path}\" -f s16le -ar 48000 -ac 1 -loglevel quiet -";

        void Handler(byte[] chunk) => onChunk(chunk);
        decoder.OnAudio += Handler;
        try
        {
            var dummyChannel = new Channel { Frequency = evt.Frequency };
            await decoder.StartAsync(dummyChannel, token);
        }
        finally
        {
            decoder.OnAudio -= Handler;
            decoder.Stop();
        }
    }

    private async Task StreamRawAsync(string path, Action<byte[]> onChunk, CancellationToken token)
    {
        var buffer = new byte[ChunkBytes];
        using var fs = File.OpenRead(path);

        // Skip the 44-byte WAV header if present.
        if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && fs.Length > 44)
            fs.Position = 44;

        int bytesRead;
        while ((bytesRead = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
        {
            var chunk = new byte[bytesRead];
            Array.Copy(buffer, chunk, bytesRead);
            onChunk(chunk);

            // Throttle to real time so playback sounds natural in the demo.
            await Task.Delay(ChunkInterval, _timeProvider, token);
        }
    }
}
