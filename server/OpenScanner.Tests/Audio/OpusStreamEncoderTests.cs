using Concentus;
using OpenScanner.Server.Audio;
using Xunit;

namespace OpenScanner.Tests.Audio;

public class OpusStreamEncoderTests
{
    private const int FrameSamples = OpusStreamEncoder.FrameSamplesPerChannel; // 960

    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Energy at a single frequency, via one Goertzel bin.</summary>
    private static double GoertzelPower(double[] samples, double hz, int sampleRate = 48000)
    {
        double coeff = 2 * Math.Cos(2 * Math.PI * hz / sampleRate);
        double s1 = 0, s2 = 0;
        foreach (var x in samples)
        {
            double s0 = x + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return s1 * s1 + s2 * s2 - coeff * s1 * s2;
    }

    private static byte[] Sine(int samples, double hz, int sampleRate = 48000, double amplitude = 0.5)
    {
        var pcm = new short[samples];
        for (int i = 0; i < samples; i++)
            pcm[i] = (short)(Math.Sin(2 * Math.PI * hz * i / sampleRate) * amplitude * short.MaxValue);
        return Pcm(pcm);
    }

    /// <summary>
    /// A 4096-byte mono chunk is 2048 samples: two complete 960-sample frames with 128 left over.
    /// This is the shape the single-channel decoder path actually emits.
    /// </summary>
    [Fact]
    public void Push_MonoChunk_EmitsCompleteFramesAndCarriesRemainder()
    {
        using var enc = new OpusStreamEncoder(channels: 1, bitrate: 24000);

        var packets = enc.Push(Sine(2048, 440));

        Assert.Equal(2, packets.Count);
        Assert.Equal(128, enc.PendingSamples);
        Assert.All(packets, p => Assert.NotEmpty(p));
    }

    /// <summary>
    /// The parallel mixer emits exactly 3840 bytes (960 stereo frames) per tick, which is exactly
    /// one Opus frame — no remainder should ever accumulate in parallel mode.
    /// </summary>
    [Fact]
    public void Push_StereoMixerChunk_EmitsExactlyOneFrameWithNoRemainder()
    {
        using var enc = new OpusStreamEncoder(channels: 2, bitrate: 40000);

        var packets = enc.Push(new byte[FrameSamples * 4]);

        Assert.Single(packets);
        Assert.Equal(0, enc.PendingSamples);
    }

    [Fact]
    public void Push_AccumulatesAcrossChunksUntilAFrameIsComplete()
    {
        using var enc = new OpusStreamEncoder(channels: 1, bitrate: 24000);

        Assert.Empty(enc.Push(Sine(500, 440)));
        Assert.Empty(enc.Push(Sine(459, 440)));
        Assert.Single(enc.Push(Sine(1, 440)));
        Assert.Equal(0, enc.PendingSamples);
    }

    /// <summary>
    /// DSDBase passes a trailing odd byte through unchanged, so chunks can end mid-sample. The
    /// low byte must be carried into the next push; dropping it would swap the byte order of every
    /// subsequent sample. Decode the result and compare against a reference encode of the same
    /// samples fed in one piece — identical framing means the split was handled correctly.
    /// </summary>
    [Fact]
    public void Push_OddLengthChunk_CarriesTheHalfSampleIntoTheNextPush()
    {
        var full = Sine(FrameSamples, 440);

        using var split = new OpusStreamEncoder(channels: 1, bitrate: 24000);
        var a = split.Push(full.AsSpan(0, 101).ToArray());  // ends mid-sample
        var b = split.Push(full.AsSpan(101).ToArray());

        using var whole = new OpusStreamEncoder(channels: 1, bitrate: 24000);
        var reference = whole.Push(full);

        Assert.Empty(a);
        Assert.Single(b);
        Assert.Single(reference);
        Assert.Equal(reference[0], b[0]);
    }

    [Fact]
    public void Push_EmptyInput_ProducesNothingAndKeepsState()
    {
        using var enc = new OpusStreamEncoder(channels: 1, bitrate: 24000);
        enc.Push(Sine(100, 440));

        Assert.Empty(enc.Push(ReadOnlySpan<byte>.Empty));
        Assert.Equal(100, enc.PendingSamples);
    }

    /// <summary>
    /// Feeding randomly-sized chunks (including odd ones) must reconstruct the same sample stream
    /// as feeding it all at once — the accumulator must never desync.
    /// </summary>
    [Fact]
    public void Push_RandomlyChunkedInput_MatchesUnchunkedEncoding()
    {
        var pcm = Sine(FrameSamples * 5, 700);

        using var whole = new OpusStreamEncoder(channels: 1, bitrate: 24000);
        var reference = whole.Push(pcm);

        using var chunked = new OpusStreamEncoder(channels: 1, bitrate: 24000);
        var rng = new Random(1234);
        var got = new List<byte[]>();
        for (int offset = 0; offset < pcm.Length;)
        {
            int take = Math.Min(rng.Next(1, 997), pcm.Length - offset);
            got.AddRange(chunked.Push(pcm.AsSpan(offset, take)));
            offset += take;
        }

        Assert.Equal(5, reference.Count);
        Assert.Equal(reference.Count, got.Count);
        for (int i = 0; i < reference.Count; i++)
            Assert.Equal(reference[i], got[i]);
    }

    [Fact]
    public void FlushWithSilence_EmitsThePartialFrameThenNothing()
    {
        using var enc = new OpusStreamEncoder(channels: 1, bitrate: 24000);
        enc.Push(Sine(400, 440));

        var tail = enc.FlushWithSilence();

        Assert.NotNull(tail);
        Assert.Equal(0, enc.PendingSamples);
        Assert.Null(enc.FlushWithSilence());
    }

    [Fact]
    public void Reset_DiscardsThePartialFrame()
    {
        using var enc = new OpusStreamEncoder(channels: 1, bitrate: 24000);
        enc.Push(Sine(400, 440));

        enc.Reset();

        Assert.Equal(0, enc.PendingSamples);
        Assert.Empty(enc.Push(Sine(959, 440)));
        Assert.Single(enc.Push(Sine(1, 440)));
    }

    /// <summary>
    /// Round-trip a 440 Hz tone through a real Opus decoder. Opus is lossy, so compare energy and
    /// dominant frequency rather than samples.
    /// </summary>
    [Fact]
    public void EncodedAudio_RoundTripsThroughAnOpusDecoder()
    {
        const int frames = 25; // 500 ms
        var pcm = Sine(FrameSamples * frames, 440);

        using var enc = new OpusStreamEncoder(channels: 1, bitrate: 24000);
        var packets = enc.Push(pcm);
        Assert.Equal(frames, packets.Count);

        var decoder = OpusCodecFactory.CreateDecoder(48000, 1);
        var decoded = new List<short>();
        var scratch = new short[FrameSamples];
        foreach (var packet in packets)
        {
            int n = decoder.Decode(packet, scratch, FrameSamples, false);
            decoded.AddRange(scratch.AsSpan(0, n).ToArray());
        }

        Assert.Equal(FrameSamples * frames, decoded.Count);

        // Skip the encoder's warm-up (~6.5 ms of algorithmic delay) before comparing energy.
        double Rms(IEnumerable<short> s) => Math.Sqrt(s.Select(v => (double)v * v).Average());
        var inputSamples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, inputSamples, 0, pcm.Length);

        double inRms = Rms(inputSamples.Skip(FrameSamples));
        double outRms = Rms(decoded.Skip(FrameSamples));
        Assert.InRange(outRms / inRms, 0.7, 1.3);

        // The tone should still be at 440 Hz: far more energy there than at an unrelated bin.
        var window = decoded.Skip(FrameSamples).Take(8192).Select(v => (double)v).ToArray();
        Assert.True(GoertzelPower(window, 440) > GoertzelPower(window, 1500) * 10,
            "decoded audio lost its 440 Hz fundamental");
    }

    /// <summary>
    /// Guards the bitrate settings: at 24 kbps a 20 ms packet averages ~60 bytes. If someone drops
    /// the configuration and Concentus falls back to its default, packets balloon and this fails.
    /// </summary>
    [Fact]
    public void MonoPacketsStayWithinTheExpectedBitrateBudget()
    {
        using var enc = new OpusStreamEncoder(channels: 1, bitrate: 24000);
        var packets = enc.Push(Sine(FrameSamples * 50, 440)); // 1 second

        double meanBytes = packets.Average(p => p.Length);
        Assert.InRange(meanBytes, 10, 120);

        // The whole point of the change: a second of mono audio is 96000 bytes as raw PCM.
        int total = packets.Sum(p => p.Length);
        Assert.True(total < 96000 / 15, $"expected >15x compression, got {96000.0 / total:F1}x");
    }

    [Fact]
    public void Constructor_RejectsUnsupportedChannelCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusStreamEncoder(channels: 0, bitrate: 24000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusStreamEncoder(channels: 3, bitrate: 24000));
    }
}
