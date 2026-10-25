namespace OpenScanner.Server.Audio;

/// <summary>
/// A chunk of live audio leaving a radio source, carrying its own format.
///
/// The channel count used to be inferred by the browser from <c>parallelChannels</c> on the
/// *control* WebSocket — a different connection with no ordering relationship to the audio, so a
/// scan-mode switch could deinterleave stereo as mono until the two sockets caught up. Attaching
/// the format to the samples at the point they are produced removes the guesswork.
/// </summary>
/// <param name="Pcm">Interleaved little-endian signed 16-bit PCM.</param>
/// <param name="Channels">1 for mono, 2 for interleaved stereo.</param>
/// <param name="SampleRate">Sample rate in Hz.</param>
public readonly record struct AudioChunk(byte[] Pcm, int Channels, int SampleRate)
{
    /// <summary>Mono 48 kHz — the single-channel decoder path.</summary>
    public static AudioChunk Mono48k(byte[] pcm) => new(pcm, 1, 48000);

    /// <summary>Interleaved stereo 48 kHz — the parallel FastScan mixer.</summary>
    public static AudioChunk Stereo48k(byte[] pcm) => new(pcm, 2, 48000);
}
