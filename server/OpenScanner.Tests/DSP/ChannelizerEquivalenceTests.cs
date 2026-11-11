using OpenScanner.Server.DSP;
using Xunit.Abstractions;

namespace OpenScanner.Tests.DSP;

/// <summary>
/// Guards the optimized <see cref="Channelizer"/> against the frozen double-precision
/// <see cref="ReferenceChannelizer"/>. The optimized path uses float delay lines and a
/// rotating-phasor NCO, so outputs may differ by rounding, but never audibly.
/// </summary>
public class ChannelizerEquivalenceTests
{
    private readonly ITestOutputHelper _out;
    public ChannelizerEquivalenceTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// Wideband u8 IQ with two FM carriers and noise; one carrier sits at +150 kHz.
    /// </summary>
    private static byte[] MakeIq(int sampleRate, double seconds, int seed = 42)
    {
        var rng = new Random(seed);
        int n = (int)(sampleRate * seconds);
        var iq = new byte[n * 2];
        double p1 = 0, p2 = 0, mod = 0;
        for (int i = 0; i < n; i++)
        {
            mod += 2 * Math.PI * 1000 / sampleRate;
            p1 += 2 * Math.PI * (150_000 + 3000 * Math.Sin(mod)) / sampleRate;
            p2 += 2 * Math.PI * (-420_000 + 2500 * Math.Sin(mod * 1.7)) / sampleRate;
            // AM on carrier 1 too, so AM-mode channels have an envelope to follow.
            double a1 = 40 * (1 + 0.5 * Math.Sin(mod * 0.8));
            double re = a1 * Math.Cos(p1) + 30 * Math.Cos(p2) + rng.NextDouble() * 20 - 10;
            double im = a1 * Math.Sin(p1) + 30 * Math.Sin(p2) + rng.NextDouble() * 20 - 10;
            iq[i * 2] = (byte)Math.Clamp(127.5 + re, 0, 255);
            iq[i * 2 + 1] = (byte)Math.Clamp(127.5 + im, 0, 255);
        }
        return iq;
    }

    private static short[] RunChunked(Func<byte[], int, byte[], int> process, byte[] iq, int chunk, int maxOut)
    {
        var result = new List<short>();
        var inBuf = new byte[chunk];
        var outBuf = new byte[maxOut];
        for (int off = 0; off < iq.Length; off += chunk)
        {
            int len = Math.Min(chunk, iq.Length - off);
            Buffer.BlockCopy(iq, off, inBuf, 0, len);
            int written = process(inBuf, len, outBuf);
            for (int i = 0; i + 1 < written; i += 2)
                result.Add((short)(outBuf[i] | (outBuf[i + 1] << 8)));
        }
        return result.ToArray();
    }

    [Theory]
    [InlineData(2_400_000, 150_000.0, false)]  // on the FM carrier
    [InlineData(2_400_000, -420_000.0, false)] // on the second carrier
    [InlineData(2_400_000, 600_000.0, false)]  // noise only (worst case for FM rounding)
    [InlineData(2_400_000, 150_000.0, true)]   // AM
    [InlineData(960_000, 150_000.0, false)]    // decim 20 = 4 x 5
    [InlineData(1_440_000, 150_000.0, false)]  // decim 30 = 6 x 5
    [InlineData(240_000, 0.0, false)]          // decim 5, single stage
    public void MatchesReference(int sampleRate, double offsetHz, bool am)
    {
        var iq = MakeIq(sampleRate, 1.0);
        var opt = new Channelizer(sampleRate, 48000, offsetHz, am);
        var refc = new ReferenceChannelizer(sampleRate, 48000, offsetHz, am);
        int maxOut = opt.MaxOutputBytes(65536);

        // Odd-ish chunk size to exercise NCO block and decimation boundaries across calls.
        var a = RunChunked(opt.ProcessIQ, iq, 65536 - 6, maxOut);
        var b = RunChunked(refc.ProcessIQ, iq, 65536 - 6, maxOut);

        Assert.Equal(b.Length, a.Length);

        int maxDiff = 0, over2 = 0;
        double sumSq = 0, refSq = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            maxDiff = Math.Max(maxDiff, d);
            if (d > 2) over2++;
            sumSq += (double)d * d;
            refSq += (double)b[i] * b[i];
        }
        double rmsDiff = Math.Sqrt(sumSq / a.Length);
        double snrDb = 10 * Math.Log10((refSq + 1) / (sumSq + 1));
        _out.WriteLine($"rate={sampleRate} off={offsetHz} am={am}: n={a.Length} maxDiff={maxDiff} " +
                       $">2LSB={over2} rmsDiff={rmsDiff:F3} SNR vs ref={snrDb:F1} dB");

        // Rounding-level agreement: sub-LSB RMS, and only rare multi-LSB outliers
        // (FM phase of near-zero noise vectors is sensitive to float rounding).
        Assert.True(rmsDiff < 1.0, $"RMS diff {rmsDiff:F3} LSB");
        Assert.True(over2 <= a.Length / 1000, $"{over2} samples differ by more than 2 LSB");
        Assert.Equal(refc.SignalPowerDb, opt.SignalPowerDb, 2);
    }

    [Fact]
    public void ChunkBoundariesDoNotChangeOutput()
    {
        var iq = MakeIq(2_400_000, 0.5);
        var whole = new Channelizer(2_400_000, 48000, 150_000);
        var chunked = new Channelizer(2_400_000, 48000, 150_000);

        var a = RunChunked(whole.ProcessIQ, iq, iq.Length, whole.MaxOutputBytes(iq.Length));
        var b = RunChunked(chunked.ProcessIQ, iq, 1234, chunked.MaxOutputBytes(iq.Length));

        Assert.Equal(a, b);
    }

    [Fact]
    public void ResetMatchesFreshInstance()
    {
        var iq = MakeIq(2_400_000, 0.25);
        var used = new Channelizer(2_400_000, 48000, 150_000);
        RunChunked(used.ProcessIQ, MakeIq(2_400_000, 0.1, seed: 7), 65536, used.MaxOutputBytes(65536));
        used.Reset();
        var fresh = new Channelizer(2_400_000, 48000, 150_000);

        var a = RunChunked(used.ProcessIQ, iq, 65536, used.MaxOutputBytes(65536));
        var b = RunChunked(fresh.ProcessIQ, iq, 65536, fresh.MaxOutputBytes(65536));

        Assert.Equal(b, a);
    }
}
