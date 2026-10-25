using Concentus;
using Concentus.Enums;

namespace OpenScanner.Server.Audio;

/// <summary>
/// Encodes a stream of arbitrary-length s16le PCM chunks into Opus packets.
///
/// Opus only accepts exact frame sizes, but the audio chunks reaching the broadcaster don't
/// align to one: the single-channel decoder path flushes whenever it has 4096 bytes *or* 40 ms
/// have elapsed (<see cref="Decoders.DSDBase"/>), while the parallel mixer emits exactly 20 ms.
/// This class buffers whatever it is handed and emits one packet per complete 20 ms frame,
/// carrying the remainder to the next push.
///
/// Not thread-safe — the underlying Concentus encoder holds mutable state and must have a single
/// caller. Callers broadcasting from more than one producer thread must serialize their pushes.
/// </summary>
public sealed class OpusStreamEncoder : IDisposable
{
    /// <summary>Samples per channel in one Opus frame: 20 ms at 48 kHz.</summary>
    public const int FrameSamplesPerChannel = 960;

    /// <summary>Largest possible Opus packet for a single frame.</summary>
    private const int MaxPacketBytes = 1275;

    private readonly IOpusEncoder _encoder;
    private readonly short[] _frame;
    private readonly byte[] _packetScratch = new byte[MaxPacketBytes];

    /// <summary>Interleaved samples buffered so far toward the next frame.</summary>
    private int _filled;

    /// <summary>
    /// Low byte of a sample whose high byte hasn't arrived yet, or -1. Chunks really can end
    /// mid-sample: <c>DSDBase.ProcessAudioStream</c> passes a trailing odd byte through unchanged,
    /// so a 40 ms flush can land on an odd boundary. Dropping the byte instead of carrying it
    /// would swap the endianness of every subsequent sample.
    /// </summary>
    private int _pendingByte = -1;

    private bool _disposed;

    /// <summary>Channel count this encoder was created for (1 = mono, 2 = interleaved stereo).</summary>
    public int Channels { get; }

    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; }

    /// <summary>Target bitrate in bits per second.</summary>
    public int Bitrate { get; }

    /// <summary>Samples per channel currently buffered toward the next frame.</summary>
    public int PendingSamples => _filled / Channels;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpusStreamEncoder"/> class.
    /// </summary>
    /// <param name="channels">1 for mono, 2 for interleaved stereo.</param>
    /// <param name="bitrate">Target bitrate in bits per second.</param>
    /// <param name="complexity">Opus complexity 0-10. Concentus is managed and slower than native
    /// libopus, so this defaults low enough to stay cheap on a Pi.</param>
    /// <param name="sampleRate">Input sample rate in Hz.</param>
    public OpusStreamEncoder(int channels, int bitrate, int complexity = 5, int sampleRate = 48000)
    {
        if (channels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channels));

        Channels = channels;
        SampleRate = sampleRate;
        Bitrate = bitrate;

        _encoder = OpusCodecFactory.CreateEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = bitrate;
        _encoder.Complexity = complexity;
        // The source is vocoder output that the client already lowpasses at 3400 Hz, so tell the
        // encoder it's speech and stop it spending bits above the voice band.
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        _encoder.MaxBandwidth = OpusBandwidth.OPUS_BANDWIDTH_WIDEBAND;
        _encoder.UseVBR = true;
        _encoder.UseConstrainedVBR = false;
        // FEC exists for lossy datagram transports. WebSockets ride TCP, which doesn't drop
        // packets — it stalls — so every FEC bit would be waste.
        _encoder.UseInbandFEC = false;
        _encoder.PacketLossPercent = 0;
        // Silence is already suppressed upstream: the mixer skips broadcasting when every channel
        // is quiet, and the decoder path simply stops producing. DTX would add nothing.
        _encoder.UseDTX = false;

        _frame = new short[FrameSamplesPerChannel * channels];
    }

    /// <summary>
    /// Appends s16le PCM and returns zero or more complete Opus packets, in order.
    /// </summary>
    /// <param name="pcm">Interleaved little-endian 16-bit PCM. May be any length, including odd.</param>
    /// <returns>One packet per complete 20 ms frame the push completed.</returns>
    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> pcm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        List<byte[]>? packets = null;
        int i = 0;

        while (i < pcm.Length)
        {
            short sample;
            if (_pendingByte >= 0)
            {
                sample = (short)((pcm[i] << 8) | _pendingByte);
                _pendingByte = -1;
                i += 1;
            }
            else if (i + 1 < pcm.Length)
            {
                sample = (short)((pcm[i + 1] << 8) | pcm[i]);
                i += 2;
            }
            else
            {
                _pendingByte = pcm[i];
                break;
            }

            _frame[_filled++] = sample;
            if (_filled == _frame.Length)
            {
                (packets ??= new List<byte[]>()).Add(EncodeFrame());
                _filled = 0;
            }
        }

        return packets ?? (IReadOnlyList<byte[]>)Array.Empty<byte[]>();
    }

    /// <summary>
    /// Zero-pads a partial frame to a full one and encodes it, so a finite buffer (the pre-roll
    /// replay) doesn't lose its last few milliseconds.
    /// </summary>
    /// <returns>The final packet, or null when nothing was buffered.</returns>
    public byte[]? FlushWithSilence()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_filled == 0 && _pendingByte < 0) return null;

        // A dangling half-sample can't be completed; discarding one byte is the only option.
        _pendingByte = -1;
        Array.Clear(_frame, _filled, _frame.Length - _filled);
        _filled = 0;
        return EncodeFrame();
    }

    /// <summary>
    /// Discards the partial frame and resets encoder state. Used across stream gaps so the
    /// encoder doesn't carry prediction state from one transmission into the next.
    /// </summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _filled = 0;
        _pendingByte = -1;
        _encoder.ResetState();
    }

    private byte[] EncodeFrame()
    {
        int len = _encoder.Encode(_frame, FrameSamplesPerChannel, _packetScratch, _packetScratch.Length);
        return _packetScratch.AsSpan(0, len).ToArray();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        (_encoder as IDisposable)?.Dispose();
    }
}
