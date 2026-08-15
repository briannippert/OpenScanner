using OpenScanner.Server.DSP;
using Xunit;

namespace OpenScanner.Tests.DSP;

/// <summary>
/// Measures what the channelizer actually rejects. Adjacent-channel rejection is the property
/// that decides whether dsd-fme sees clean C4FM symbols or a neighbouring transmitter's, and it
/// is measured on <see cref="Channelizer.SignalPowerDb"/> — pre-demodulation complex power —
/// because the FM discriminator is amplitude-blind. Feeding it a lone attenuated carrier would
/// produce full-scale audio no matter how far down the carrier was, so a post-demod measurement
/// cannot see a filter at all.
/// </summary>
public class ChannelizerSelectivityTests
{
    private const int InputRate = 960000;
    private const int OutputRate = 48000;

    /// <summary>P25 and DMR both use 12.5 kHz channel spacing.</summary>
    private const double AdjacentChannelHz = 12500;

    /// <summary>
    /// Generates a constant-amplitude carrier at <paramref name="toneOffsetHz"/> from the SDR
    /// centre, as unsigned 8-bit interleaved IQ (what rtl_sdr emits).
    /// </summary>
    private static byte[] Carrier(double toneOffsetHz, int samples, double amplitude = 100)
    {
        var iq = new byte[samples * 2];
        double phase = 0;
        double inc = 2.0 * Math.PI * toneOffsetHz / InputRate;
        for (int i = 0; i < samples; i++)
        {
            iq[i * 2] = (byte)Math.Clamp(127.5 + amplitude * Math.Cos(phase), 0, 255);
            iq[i * 2 + 1] = (byte)Math.Clamp(127.5 + amplitude * Math.Sin(phase), 0, 255);
            phase += inc;
            if (phase > Math.PI) phase -= 2.0 * Math.PI;
        }
        return iq;
    }

    /// <summary>
    /// Runs a carrier through a channelizer tuned to <paramref name="tunedOffsetHz"/> and returns
    /// the settled in-channel power. Uses a full second so the ~100 ms power window is filled
    /// several times over and filter start-up transients have washed out.
    /// </summary>
    private static double PowerDbOf(double tunedOffsetHz, double carrierOffsetHz, double channelCutoffHz)
    {
        var ch = new Channelizer(InputRate, OutputRate, tunedOffsetHz, channelCutoffHz: channelCutoffHz);
        var iq = Carrier(carrierOffsetHz, InputRate);
        var output = new byte[ch.MaxOutputBytes(iq.Length)];
        ch.ProcessIQ(iq, iq.Length, output);
        return ch.SignalPowerDb;
    }

    /// <summary>
    /// The headline guarantee. A transmitter one channel away (12.5 kHz) must be pushed deep into
    /// the stopband rather than sitting in the passband alongside the wanted signal.
    ///
    /// This fails on the pre-2026 filter design, which cut off at 19.2 kHz with a ~63 kHz Blackman
    /// transition — the adjacent channel was inside the passband and essentially unattenuated.
    /// </summary>
    [Fact]
    public void AdjacentChannel_IsRejectedByAtLeast60dB()
    {
        double onChannel = PowerDbOf(tunedOffsetHz: 100000, carrierOffsetHz: 100000, channelCutoffHz: 6250);
        double adjacent = PowerDbOf(tunedOffsetHz: 100000, carrierOffsetHz: 100000 + AdjacentChannelHz, channelCutoffHz: 6250);

        double rejectionDb = onChannel - adjacent;
        Assert.True(rejectionDb >= 60,
            $"adjacent channel at {AdjacentChannelHz / 1000:F1} kHz rejected by only {rejectionDb:F1} dB " +
            $"(on-channel {onChannel:F1} dB, adjacent {adjacent:F1} dB)");
    }

    /// <summary>
    /// The wanted signal must survive the new filter intact — a selectivity win that also
    /// attenuated the passband would be a net loss.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(3000)]
    public void InBandSignal_PassesUnattenuated(double toneHz)
    {
        double reference = PowerDbOf(100000, 100000, channelCutoffHz: 6250);
        double shifted = PowerDbOf(100000, 100000 + toneHz, channelCutoffHz: 6250);

        Assert.True(Math.Abs(reference - shifted) < 1.0,
            $"in-band tone at {toneHz} Hz lost {reference - shifted:F2} dB");
    }

    /// <summary>
    /// Narrowing the channel filter to the signal's real occupied bandwidth cuts the noise that
    /// reaches the demodulator. Going from the old 19.2 kHz cutoff to 6.25 kHz is a 3.07x
    /// reduction in noise bandwidth, so roughly 4.9 dB — this asserts the bulk of it lands.
    /// </summary>
    [Fact]
    public void NarrowerChannel_LowersNoiseBandwidth()
    {
        var rng = new Random(20260814);
        var noise = new byte[InputRate * 2];
        for (int i = 0; i < noise.Length; i++)
            noise[i] = (byte)Math.Clamp(127.5 + 40 * (rng.NextDouble() * 2 - 1), 0, 255);

        double wide = NoisePowerDb(channelCutoffHz: 19200);
        double narrow = NoisePowerDb(channelCutoffHz: 6250);

        Assert.True(narrow < wide - 3.5,
            $"expected >=3.5 dB less noise through the narrow filter, got {wide - narrow:F2} dB");

        double NoisePowerDb(double channelCutoffHz)
        {
            var ch = new Channelizer(InputRate, OutputRate, 100000, channelCutoffHz: channelCutoffHz);
            var output = new byte[ch.MaxOutputBytes(noise.Length)];
            ch.ProcessIQ(noise, noise.Length, output);
            return ch.SignalPowerDb;
        }
    }

    /// <summary>
    /// The recursive NCO trades a per-sample Math.Cos/Math.Sin pair for one complex multiply,
    /// which is only sound if its magnitude does not creep. A constant-amplitude carrier must
    /// therefore read the same power at the end of a long run as at the start.
    /// </summary>
    [Fact]
    public void RecursiveNco_DoesNotDriftOverALongRun()
    {
        var ch = new Channelizer(InputRate, OutputRate, 100000, channelCutoffHz: 6250);
        var iq = Carrier(100000, InputRate);
        var output = new byte[ch.MaxOutputBytes(iq.Length)];

        ch.ProcessIQ(iq, iq.Length, output);
        double early = ch.SignalPowerDb;

        // ~20 more seconds of samples: 19.2M complex multiplies for the rotator to drift in.
        for (int i = 0; i < 20; i++) ch.ProcessIQ(iq, iq.Length, output);
        double late = ch.SignalPowerDb;

        Assert.True(Math.Abs(early - late) < 0.05,
            $"NCO magnitude drifted: {early:F4} dB -> {late:F4} dB");
    }

    /// <summary>
    /// P25 C4FM peaks at ±1800 Hz. Scaling for the 5 kHz deviation of analog voice left digital
    /// audio at ~28% of full scale — about 11 dB of headroom handed to the decoder unused.
    /// </summary>
    [Fact]
    public void DeviationScaling_FillsTheRangeForNarrowDeviation()
    {
        Assert.InRange(OutputRms(deviationHz: 1800, modulationDeviationHz: 1800), 0.45, 0.70);
    }

    /// <summary>Analog FM keeps its existing 5 kHz assumption, so this is a no-change guard.</summary>
    [Fact]
    public void DeviationScaling_LeavesAnalogUnchanged()
    {
        Assert.InRange(OutputRms(deviationHz: 5000, modulationDeviationHz: 5000), 0.45, 0.70);
    }

    /// <summary>
    /// Generates an FM carrier deviated by <paramref name="modulationDeviationHz"/> at 1 kHz,
    /// channelizes it, and returns the output RMS as a fraction of full scale. Full deviation
    /// maps to 0.8, so a correctly scaled sine reads 0.8/sqrt(2) = 0.566.
    ///
    /// RMS rather than peak: the source is 8-bit quantized, so the discriminator throws
    /// occasional outliers that a peak measurement would latch onto and report as clipping.
    /// </summary>
    private static double OutputRms(double deviationHz, double modulationDeviationHz)
    {
        const double toneHz = 1000;
        int samples = InputRate / 4;
        var iq = new byte[samples * 2];
        double carrierPhase = 0, tonePhase = 0;
        double toneInc = 2.0 * Math.PI * toneHz / InputRate;

        for (int i = 0; i < samples; i++)
        {
            double instantaneous = modulationDeviationHz * Math.Sin(tonePhase);
            carrierPhase += 2.0 * Math.PI * instantaneous / InputRate;
            tonePhase += toneInc;
            iq[i * 2] = (byte)Math.Clamp(127.5 + 100 * Math.Cos(carrierPhase), 0, 255);
            iq[i * 2 + 1] = (byte)Math.Clamp(127.5 + 100 * Math.Sin(carrierPhase), 0, 255);
        }

        var ch = new Channelizer(InputRate, OutputRate, 0, channelCutoffHz: 6250, deviationHz: deviationHz);
        var output = new byte[ch.MaxOutputBytes(iq.Length)];
        int written = ch.ProcessIQ(iq, iq.Length, output);

        // Skip the filter's start-up transient before measuring.
        double sumSquares = 0;
        int count = 0;
        for (int i = 2000; i + 1 < written; i += 2)
        {
            short sample = (short)(output[i] | (output[i + 1] << 8));
            sumSquares += (double)sample * sample;
            count++;
        }
        return Math.Sqrt(sumSquares / count) / 32000.0;
    }

    /// <summary>
    /// RTL-SDR dongles put a large DC spike at the tuner centre. Without a DC blocker it lands
    /// inside whichever channel happens to sit at the centre frequency, which is exactly the
    /// case multi-channel FastScan can produce when it centres on the midpoint of the set.
    /// </summary>
    [Fact]
    public void DcBlocker_RemovesTheCentreSpike()
    {
        // A constant IQ offset with no modulation: pure DC, i.e. the dongle's spike.
        var iq = new byte[InputRate * 2];
        for (int i = 0; i < InputRate; i++)
        {
            iq[i * 2] = 180;       // well away from the 127.5 midpoint
            iq[i * 2 + 1] = 150;
        }

        var ch = new Channelizer(InputRate, OutputRate, 0, channelCutoffHz: 6250);
        var output = new byte[ch.MaxOutputBytes(iq.Length)];
        ch.ProcessIQ(iq, iq.Length, output);

        // Reference is an in-band carrier offset from DC — a carrier *at* DC is precisely what
        // the blocker is meant to remove, so it cannot serve as the yardstick.
        double realSignal = PowerDbOf(tunedOffsetHz: 0, carrierOffsetHz: 2000, channelCutoffHz: 6250);
        Assert.True(ch.SignalPowerDb < realSignal - 40,
            $"DC spike only suppressed to {ch.SignalPowerDb:F1} dB against a {realSignal:F1} dB carrier");
    }
}
