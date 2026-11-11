using OpenScanner.Server.DSP;
using Xunit;

namespace OpenScanner.Tests.DSP;

/// <summary>
/// Carrier detection decides whether the scanner ever hears a transmission, so these tests
/// pin the two properties that decide it: how far into the noise a weak signal can be and
/// still be found, and whether one strong signal can hide the others.
/// </summary>
public class SpectrumEstimatorTests
{
    private const int SampleRate = 1024000;
    private const int FftSize = 1024;

    /// <summary>
    /// Builds an IQ buffer of complex Gaussian noise with optional carriers on top.
    /// Amplitudes are relative to the 8-bit full scale.
    /// </summary>
    private static byte[] Scene(int samples, double noiseAmplitude, params (double OffsetHz, double Amplitude)[] carriers)
    {
        var rng = new Random(20260814);
        var iq = new byte[samples * 2];
        var phases = new double[carriers.Length];

        for (int i = 0; i < samples; i++)
        {
            // Box-Muller for Gaussian noise; uniform noise would have the wrong tail behaviour
            // for a median-vs-mean comparison to mean anything.
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            double mag = noiseAmplitude * Math.Sqrt(-2.0 * Math.Log(u1));
            double sampleI = mag * Math.Cos(2 * Math.PI * u2);
            double sampleQ = mag * Math.Sin(2 * Math.PI * u2);

            for (int c = 0; c < carriers.Length; c++)
            {
                sampleI += carriers[c].Amplitude * Math.Cos(phases[c]);
                sampleQ += carriers[c].Amplitude * Math.Sin(phases[c]);
                phases[c] += 2.0 * Math.PI * carriers[c].OffsetHz / SampleRate;
            }

            iq[i * 2] = (byte)Math.Clamp(127.5 + sampleI * 127.5, 0, 255);
            iq[i * 2 + 1] = (byte)Math.Clamp(127.5 + sampleQ * 127.5, 0, 255);
        }
        return iq;
    }

    private static SpectrumEstimate Estimate(byte[] iq, int segments = 16) =>
        SpectrumEstimator.Estimate(iq, iq.Length, SampleRate, FftSize, segments)!;

    /// <summary>
    /// Levels are absolute rather than relative to an empirical fudge factor, so the same
    /// carrier reads the same dB whatever FFT size measured it. This is what lets the squelch
    /// setting mean something fixed; the previous code subtracted a hardcoded 20 dB from raw
    /// FFT magnitudes, so changing the FFT size silently changed what the setting meant.
    ///
    /// A full-scale carrier reads +1.76 dB, not 0: integrating the channel picks up the whole
    /// Hann main lobe, whose 0.5/1.0/0.5 amplitude taper carries 1.5x the peak bin's power.
    /// That is the correct behaviour for an integrating measurement — the constant is only
    /// asserted here so a future change to the window or normalization cannot pass unnoticed.
    /// </summary>
    [Fact]
    public void CarrierLevel_IsIndependentOfFftSize()
    {
        var levels = new[] { 256, 1024, 4096 }.Select(fftSize =>
        {
            var iq = Scene(fftSize * 4, noiseAmplitude: 0, (OffsetHz: 100000, Amplitude: 1.0));
            var spectrum = SpectrumEstimator.Estimate(iq, iq.Length, SampleRate, fftSize, 4)!;
            int bin = spectrum.BinForOffset(100000);
            return spectrum.ChannelPowerDb(bin, spectrum.HalfWidthBins(12500));
        }).ToArray();

        Assert.True(levels.Max() - levels.Min() < 0.25,
            $"level varied with FFT size: [{string.Join(", ", levels.Select(l => l.ToString("F3")))}]");

        // 10*log10(1.5) = 1.761 dB of Hann main-lobe energy above the peak bin.
        Assert.All(levels, level => Assert.InRange(level, 1.5, 2.0));
    }

    /// <summary>
    /// The mean-vs-median fix. A loud carrier in the band must not raise the estimated noise
    /// floor, because doing so subtracts directly from every other channel's SNR — the
    /// scanner would go deaf to weak signals exactly when a strong one was up.
    /// </summary>
    [Fact]
    public void StrongCarrier_DoesNotRaiseTheNoiseFloor()
    {
        var quiet = Estimate(Scene(FftSize * 16, noiseAmplitude: 0.02));
        var loud = Estimate(Scene(FftSize * 16, noiseAmplitude: 0.02, (OffsetHz: 200000, Amplitude: 0.8)));

        Assert.True(Math.Abs(quiet.NoiseFloorDb - loud.NoiseFloorDb) < 1.0,
            $"noise floor moved {loud.NoiseFloorDb - quiet.NoiseFloorDb:F2} dB when a carrier appeared");
    }

    /// <summary>
    /// A weak carrier alongside a strong one must still clear a 10 dB squelch. This is the
    /// scenario the old mean-based floor failed: the strong carrier inflated the reference and
    /// pushed the weak one's apparent SNR below threshold.
    /// </summary>
    [Fact]
    public void WeakCarrier_IsFoundAlongsideAStrongOne()
    {
        var spectrum = Estimate(Scene(
            FftSize * 16,
            noiseAmplitude: 0.02,
            (OffsetHz: 200000, Amplitude: 0.8),
            (OffsetHz: -150000, Amplitude: 0.02)));

        int weakBin = spectrum.BinForOffset(-150000);
        double weakSnr = spectrum.ChannelSnrDb(weakBin, spectrum.HalfWidthBins(12500));

        Assert.True(weakSnr > 10, $"weak carrier only reached {weakSnr:F1} dB SNR");
    }

    /// <summary>
    /// Averaging is what buys the detection margin. More segments must produce a measurably
    /// steadier noise floor — this compares the spread of the floor across independent buffers.
    /// </summary>
    [Fact]
    public void MoreSegments_ProduceASteadierNoiseFloor()
    {
        double spread1 = NoiseFloorSpread(segments: 1);
        double spread16 = NoiseFloorSpread(segments: 16);

        Assert.True(spread16 < spread1,
            $"averaging did not steady the estimate: 1 segment {spread1:F3} dB, 16 segments {spread16:F3} dB");

        static double NoiseFloorSpread(int segments)
        {
            var floors = new List<double>();
            for (int trial = 0; trial < 8; trial++)
            {
                var rng = new Random(trial);
                var iq = new byte[FftSize * 16 * 2];
                for (int i = 0; i < iq.Length; i++)
                    iq[i] = (byte)Math.Clamp(127.5 + 0.02 * 127.5 * (rng.NextDouble() * 2 - 1), 0, 255);
                floors.Add(SpectrumEstimator.Estimate(iq, iq.Length, SampleRate, FftSize, segments)!.NoiseFloorDb);
            }
            return floors.Max() - floors.Min();
        }
    }

    /// <summary>
    /// Scalloping: a carrier landing between bin centres splits its energy across neighbours.
    /// Integrating the channel's bins recovers it; reading the single nearest bin does not.
    /// </summary>
    [Fact]
    public void OffCentreCarrier_IsNotUnderReported()
    {
        double binWidth = SampleRate / (double)FftSize;
        double onBin = MeasuredPower(100 * binWidth);
        double betweenBins = MeasuredPower(100.5 * binWidth);

        Assert.True(Math.Abs(onBin - betweenBins) < 1.0,
            $"carrier between bins under-reported by {onBin - betweenBins:F2} dB");

        static double MeasuredPower(double offsetHz)
        {
            var iq = Scene(FftSize * 8, noiseAmplitude: 0, (offsetHz, 0.5));
            var spectrum = Estimate(iq, segments: 8);
            int bin = spectrum.BinForOffset(offsetHz);
            return spectrum.ChannelPowerDb(bin, spectrum.HalfWidthBins(12500));
        }
    }

    [Fact]
    public void BinForOffset_MapsCentreAndEdges()
    {
        var spectrum = Estimate(Scene(FftSize * 16, noiseAmplitude: 0.02));

        Assert.Equal(FftSize / 2, spectrum.BinForOffset(0));
        Assert.Equal(-1, spectrum.BinForOffset(SampleRate));      // far outside the window
        Assert.Equal(1000.0, spectrum.BinWidthHz, 6);             // 1.024 MHz / 1024
    }

    [Fact]
    public void ShortBuffer_YieldsNoEstimateRatherThanThrowing()
    {
        var tiny = new byte[64];
        Assert.Null(SpectrumEstimator.Estimate(tiny, tiny.Length, SampleRate, FftSize));
    }
}
