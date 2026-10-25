using System.Numerics;

namespace OpenScanner.Server.DSP;

/// <summary>
/// A power spectrum of one IQ buffer, plus the statistics carrier detection needs from it.
///
/// Levels are normalized so that a full-scale complex carrier reads 0 dB regardless of FFT
/// size. That matters because the previous implementation subtracted an empirical constant
/// ("Normalize (Empirical)", -20 dB) from raw FFT magnitudes, which made the squelch setting
/// depend on the FFT size and on nothing the user could see.
/// </summary>
public sealed class SpectrumEstimate
{
    /// <summary>Linear power per bin, DC-centred (index <c>FftSize/2</c> is the tuner frequency).</summary>
    private readonly double[] _power;

    /// <summary>Number of bins.</summary>
    public int FftSize => _power.Length;

    /// <summary>Hz per bin.</summary>
    public double BinWidthHz { get; }

    /// <summary>
    /// Median bin power, in dB. The median rather than the mean: a single strong carrier — or
    /// the dongle's DC spike — drags a mean upward and suppresses the computed SNR of every
    /// other channel, which is exactly when detection matters most.
    /// </summary>
    public double NoiseFloorDb { get; }

    /// <summary>Median bin power, linear.</summary>
    private readonly double _noiseFloor;

    internal SpectrumEstimate(double[] power, double binWidthHz)
    {
        _power = power;
        BinWidthHz = binWidthHz;

        var sorted = (double[])power.Clone();
        Array.Sort(sorted);
        _noiseFloor = sorted[sorted.Length / 2];
        NoiseFloorDb = ToDb(_noiseFloor);
    }

    /// <summary>Power spectrum in dB, DC-centred, for display.</summary>
    public double[] ToDbArray()
    {
        var db = new double[_power.Length];
        for (int i = 0; i < _power.Length; i++) db[i] = ToDb(_power[i]);
        return db;
    }

    /// <summary>Bin index for a frequency offset from the tuner centre, or -1 if out of range.</summary>
    public int BinForOffset(double offsetHz)
    {
        int bin = (int)Math.Round(offsetHz / BinWidthHz + _power.Length / 2.0);
        return bin >= 0 && bin < _power.Length ? bin : -1;
    }

    /// <summary>How many bins each side of centre a channel of the given width occupies.</summary>
    public int HalfWidthBins(double channelBandwidthHz) =>
        Math.Max(0, (int)Math.Round(channelBandwidthHz / 2.0 / BinWidthHz));

    /// <summary>
    /// Signal-to-noise ratio of a channel, in dB: total power across the bins the channel
    /// occupies, against the same number of bins' worth of noise floor.
    ///
    /// Integrating the channel rather than sampling its centre bin is what removes scalloping
    /// loss — a carrier landing between bin centres splits its energy across neighbours and a
    /// single-bin reading under-reports it by up to ~3.9 dB, which is the difference between
    /// detecting a weak signal and walking past it.
    /// </summary>
    public double ChannelSnrDb(int centerBin, int halfWidthBins)
    {
        double sum = 0;
        int counted = 0;
        for (int b = centerBin - halfWidthBins; b <= centerBin + halfWidthBins; b++)
        {
            if (b < 0 || b >= _power.Length) continue;
            sum += _power[b];
            counted++;
        }
        if (counted == 0) return double.NegativeInfinity;
        return ToDb(sum / (counted * _noiseFloor));
    }

    /// <summary>Absolute power of a channel, in dB relative to a full-scale carrier.</summary>
    public double ChannelPowerDb(int centerBin, int halfWidthBins)
    {
        double sum = 0;
        for (int b = centerBin - halfWidthBins; b <= centerBin + halfWidthBins; b++)
        {
            if (b < 0 || b >= _power.Length) continue;
            sum += _power[b];
        }
        return ToDb(sum);
    }

    private static double ToDb(double linearPower) => 10.0 * Math.Log10(linearPower + 1e-20);
}

/// <summary>
/// Welch-averaged power spectrum estimation over an RTL-SDR IQ buffer.
/// </summary>
public static class SpectrumEstimator
{
    /// <summary>Hann window coherent gain, needed to normalize levels back to full scale.</summary>
    private const double HannCoherentGain = 0.5;

    /// <summary>
    /// Estimates the power spectrum by averaging FFTs of successive segments of the buffer.
    ///
    /// Averaging is the whole point. A single FFT of one segment has ~5.6 dB of standard
    /// deviation per bin, so weak carriers disappear into the variance and the detector has to
    /// be told to wait for repeat hits before believing anything. Averaging N segments divides
    /// that variance by N: 16 segments is a 4x reduction in standard deviation, roughly 6 dB of
    /// usable detection margin, recovered from data that was already in the buffer and being
    /// thrown away — the previous code used 256 of the 32768 samples it was handed.
    /// </summary>
    /// <param name="iq">Unsigned 8-bit interleaved IQ, as rtl_sdr emits.</param>
    /// <param name="length">Valid bytes in <paramref name="iq"/>.</param>
    /// <param name="sampleRate">Capture rate in Hz.</param>
    /// <param name="fftSize">Bins per FFT; must be a power of two.</param>
    /// <param name="maxSegments">Upper bound on segments to average.</param>
    /// <returns>The estimate, or null when the buffer is too short for even one segment.</returns>
    public static SpectrumEstimate? Estimate(
        byte[] iq, int length, int sampleRate, int fftSize = 1024, int maxSegments = 16)
    {
        int availableSamples = length / 2;
        int segments = Math.Min(maxSegments, availableSamples / fftSize);
        if (segments < 1) return null;

        var window = BuildHannWindow(fftSize);
        var accumulated = new double[fftSize];
        var scratch = new Complex[fftSize];

        // Normalizes an FFT bin so a full-scale complex carrier reads 1.0 (0 dB).
        double scale = 1.0 / (fftSize * HannCoherentGain);

        for (int s = 0; s < segments; s++)
        {
            int baseByte = s * fftSize * 2;
            for (int i = 0; i < fftSize; i++)
            {
                double iSample = (iq[baseByte + i * 2] - 127.5) / 127.5;
                double qSample = (iq[baseByte + i * 2 + 1] - 127.5) / 127.5;
                scratch[i] = new Complex(iSample * window[i], qSample * window[i]);
            }

            FftSharp.FFT.Forward(scratch);

            // Accumulate DC-centred, so the caller never has to think about FFT ordering.
            for (int i = 0; i < fftSize; i++)
            {
                double magnitude = scratch[i].Magnitude * scale;
                accumulated[(i + fftSize / 2) % fftSize] += magnitude * magnitude;
            }
        }

        for (int i = 0; i < fftSize; i++) accumulated[i] /= segments;
        return new SpectrumEstimate(accumulated, sampleRate / (double)fftSize);
    }

    private static double[] BuildHannWindow(int size)
    {
        var window = new double[size];
        for (int i = 0; i < size; i++)
            window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (size - 1)));
        return window;
    }
}
