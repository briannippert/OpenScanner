namespace OpenScanner.Server.DSP;

/// <summary>
/// Isolates a single narrowband channel from a wideband IQ stream.
/// Pipeline: NCO frequency shift -> two-stage FIR decimation -> FM/AM demodulation.
/// Produces 48 kHz s16le PCM audio suitable for feeding to decoders.
/// </summary>
public class Channelizer
{
    private readonly int _inputRate;
    private readonly int _outputRate;
    private readonly double _offsetHz;
    private readonly bool _amMode;

    // NCO (Numerically Controlled Oscillator)
    private double _ncoPhase;
    private readonly double _ncoPhaseInc;

    // Stage 1 decimation
    private readonly int _decim1;
    private readonly double[] _filter1;
    private readonly double[] _delayI1;
    private readonly double[] _delayQ1;
    private int _writePtr1;
    private int _decimCount1;

    // Stage 2 decimation
    private readonly int _decim2;
    private readonly double[] _filter2;
    private readonly double[] _delayI2;
    private readonly double[] _delayQ2;
    private int _writePtr2;
    private int _decimCount2;

    // FM demodulator state (previous IQ sample for phase differencing)
    private double _prevI;
    private double _prevQ;

    // AM DC removal (simple single-pole IIR highpass)
    private double _amDcEstimate;
    private const double AmDcAlpha = 0.995;

    // FM scaling: convert phase-diff to normalized audio
    private readonly double _fmScale;

    // Signal power metering (IQ magnitude before demodulation)
    private double _iqPowerAccum;
    private int _iqPowerCount;
    private double _signalPowerDb = -90.0;
    private const int IqPowerWindow = 4800; // ~100ms at 48kHz output rate

    /// <summary>
    /// Latest measured RF signal power in dB (IQ magnitude before demodulation).
    /// Updated every ~100ms. Useful for signal strength metering.
    /// Noise floor is typically around -30 to -40 dB; a carrier reads -5 to 0 dB.
    /// </summary>
    public double SignalPowerDb => _signalPowerDb;

    /// <summary>
    /// Maximum output bytes for a given input length (for buffer pre-allocation).
    /// </summary>
    public int MaxOutputBytes(int inputBytes)
    {
        int iqSamples = inputBytes / 2;
        int totalDecim = _decim1 * _decim2;
        int maxOutputSamples = (iqSamples / totalDecim) + 2; // +2 for rounding safety
        return maxOutputSamples * 2; // 2 bytes per s16le sample
    }

    /// <summary>
    /// Creates a channelizer for a single channel.
    /// </summary>
    /// <param name="inputRate">RTL-SDR sample rate in Hz (must be integer multiple of outputRate)</param>
    /// <param name="outputRate">Desired audio output rate in Hz (typically 48000)</param>
    /// <param name="offsetHz">Frequency offset of target channel from SDR center, in Hz</param>
    /// <param name="amMode">True for AM demodulation, false for FM</param>
    public Channelizer(int inputRate, int outputRate, double offsetHz, bool amMode = false)
    {
        _inputRate = inputRate;
        _outputRate = outputRate;
        _offsetHz = offsetHz;
        _amMode = amMode;

        int totalDecim = inputRate / outputRate;
        if (inputRate % outputRate != 0)
            throw new ArgumentException(
                $"Input rate {inputRate} must be an integer multiple of output rate {outputRate}");
        if (totalDecim < 1)
            throw new ArgumentException(
                $"Input rate {inputRate} must be >= output rate {outputRate}");

        // Factor total decimation into two stages for computational efficiency.
        // Stage 2 targets factor 5 (giving 240 kHz intermediate from 48 kHz output).
        // If total decimation is small, use single stage (decim2 = 1).
        if (totalDecim >= 10 && totalDecim % 5 == 0)
        {
            _decim2 = 5;
            _decim1 = totalDecim / 5;
        }
        else if (totalDecim > 6)
        {
            _decim2 = FindBestFactor(totalDecim);
            _decim1 = totalDecim / _decim2;
        }
        else
        {
            _decim1 = totalDecim;
            _decim2 = 1;
        }

        int intermediateRate = inputRate / _decim1;

        // NCO phase increment per input sample (negative for downconversion)
        _ncoPhaseInc = -2.0 * Math.PI * offsetHz / inputRate;

        // Design decimation filters using windowed sinc method.
        // Cutoff at 80% of the output Nyquist to provide transition band.
        double cutoff1Normalized = 0.8 * (intermediateRate / 2.0) / inputRate;
        int taps1 = Math.Max(4 * _decim1, 32) | 1; // Ensure odd number of taps
        _filter1 = DesignLowPass(cutoff1Normalized, taps1);
        _delayI1 = new double[taps1];
        _delayQ1 = new double[taps1];

        if (_decim2 > 1)
        {
            double cutoff2Normalized = 0.8 * (outputRate / 2.0) / intermediateRate;
            int taps2 = Math.Max(4 * _decim2, 16) | 1;
            _filter2 = DesignLowPass(cutoff2Normalized, taps2);
            _delayI2 = new double[taps2];
            _delayQ2 = new double[taps2];
        }
        else
        {
            _filter2 = Array.Empty<double>();
            _delayI2 = Array.Empty<double>();
            _delayQ2 = Array.Empty<double>();
        }

        // FM gain: scale atan2 output (radians) so ~5 kHz deviation maps to ~80% of s16le range.
        // atan2 output for 5 kHz deviation at outputRate: 2*pi*5000/outputRate radians
        // We want that to map to ~0.8, so: scale = 0.8 / (2*pi*5000/outputRate)
        double typicalDeviation = 5000.0; // 5 kHz for NFM / P25
        _fmScale = 0.8 * outputRate / (2.0 * Math.PI * typicalDeviation);
    }

    /// <summary>
    /// Process a chunk of wideband unsigned 8-bit IQ data and produce narrowband s16le audio.
    /// Call repeatedly with consecutive chunks; internal state is maintained between calls.
    /// </summary>
    /// <param name="iq">RTL-SDR output: unsigned 8-bit interleaved I,Q pairs</param>
    /// <param name="length">Number of bytes to process (must be even)</param>
    /// <param name="output">Pre-allocated buffer for s16le PCM output</param>
    /// <returns>Number of bytes written to output</returns>
    public int ProcessIQ(byte[] iq, int length, byte[] output)
    {
        int outIdx = 0;

        for (int i = 0; i + 1 < length; i += 2)
        {
            // Convert unsigned 8-bit to float [-1, 1]
            double rawI = (iq[i] - 127.5) * (1.0 / 127.5);
            double rawQ = (iq[i + 1] - 127.5) * (1.0 / 127.5);

            // NCO frequency shift (complex multiply by e^(j*phase))
            double cosP = Math.Cos(_ncoPhase);
            double sinP = Math.Sin(_ncoPhase);
            double sI = rawI * cosP - rawQ * sinP;
            double sQ = rawI * sinP + rawQ * cosP;
            _ncoPhase += _ncoPhaseInc;
            if (_ncoPhase > Math.PI) _ncoPhase -= 2.0 * Math.PI;
            else if (_ncoPhase < -Math.PI) _ncoPhase += 2.0 * Math.PI;

            // Stage 1: push into delay line
            _delayI1[_writePtr1] = sI;
            _delayQ1[_writePtr1] = sQ;
            _writePtr1 = (_writePtr1 + 1) % _filter1.Length;

            _decimCount1++;
            if (_decimCount1 < _decim1) continue;
            _decimCount1 = 0;

            // Stage 1: compute FIR output
            double fI1 = 0, fQ1 = 0;
            int rp = _writePtr1;
            for (int k = 0; k < _filter1.Length; k++)
            {
                rp--;
                if (rp < 0) rp += _filter1.Length;
                fI1 += _filter1[k] * _delayI1[rp];
                fQ1 += _filter1[k] * _delayQ1[rp];
            }

            // Stage 2 (if applicable)
            double fI, fQ;
            if (_decim2 > 1)
            {
                _delayI2[_writePtr2] = fI1;
                _delayQ2[_writePtr2] = fQ1;
                _writePtr2 = (_writePtr2 + 1) % _filter2.Length;

                _decimCount2++;
                if (_decimCount2 < _decim2) continue;
                _decimCount2 = 0;

                fI = 0; fQ = 0;
                int rp2 = _writePtr2;
                for (int k = 0; k < _filter2.Length; k++)
                {
                    rp2--;
                    if (rp2 < 0) rp2 += _filter2.Length;
                    fI += _filter2[k] * _delayI2[rp2];
                    fQ += _filter2[k] * _delayQ2[rp2];
                }
            }
            else
            {
                fI = fI1;
                fQ = fQ1;
            }

            // Measure IQ power before demodulation (true RF signal strength)
            double mag2 = fI * fI + fQ * fQ;
            _iqPowerAccum += mag2;
            _iqPowerCount++;
            if (_iqPowerCount >= IqPowerWindow)
            {
                double rmsMag = Math.Sqrt(_iqPowerAccum / _iqPowerCount);
                _signalPowerDb = 20.0 * Math.Log10(rmsMag + 1e-12);
                _iqPowerAccum = 0;
                _iqPowerCount = 0;
            }

            // Demodulate
            double sample;
            if (_amMode)
            {
                double envelope = Math.Sqrt(fI * fI + fQ * fQ);
                // Remove DC with single-pole IIR highpass
                _amDcEstimate = AmDcAlpha * _amDcEstimate + (1.0 - AmDcAlpha) * envelope;
                sample = (envelope - _amDcEstimate) * 4.0; // Gain to fill range
            }
            else
            {
                // FM quadrature discriminator: angle between consecutive IQ samples
                double dot = fI * _prevI + fQ * _prevQ;
                double cross = fQ * _prevI - fI * _prevQ;
                double phaseD = Math.Atan2(cross, dot);
                sample = phaseD * _fmScale;
            }
            _prevI = fI;
            _prevQ = fQ;

            // Clamp and convert to s16le
            if (sample > 1.0) sample = 1.0;
            else if (sample < -1.0) sample = -1.0;
            short pcm = (short)(sample * 32000);
            output[outIdx++] = (byte)(pcm & 0xFF);
            output[outIdx++] = (byte)((pcm >> 8) & 0xFF);
        }

        return outIdx;
    }

    /// <summary>
    /// Reset internal state (NCO phase, filter delay lines, demod state).
    /// Call when switching to a new IQ stream segment.
    /// </summary>
    public void Reset()
    {
        _ncoPhase = 0;
        Array.Clear(_delayI1); Array.Clear(_delayQ1); _writePtr1 = 0; _decimCount1 = 0;
        Array.Clear(_delayI2); Array.Clear(_delayQ2); _writePtr2 = 0; _decimCount2 = 0;
        _prevI = 0; _prevQ = 0;
        _amDcEstimate = 0;
        _iqPowerAccum = 0; _iqPowerCount = 0; _signalPowerDb = -90.0;
    }

    /// <summary>
    /// Select the best sample rate for parallel scanning that gives integer decimation to 48 kHz
    /// and covers the required frequency spread.
    /// </summary>
    /// <param name="spreadHz">Total channel spread in Hz (max freq - min freq)</param>
    /// <returns>Sample rate in Hz</returns>
    public static int SelectSampleRate(double spreadHz)
    {
        // Valid RTL-SDR rates that give integer decimation to 48 kHz.
        // Listed from smallest to largest; pick the first that covers the spread with margin.
        int[] validRates = { 960000, 1440000, 1920000, 2400000 };
        double required = spreadHz + 100000; // 50 kHz margin on each side

        foreach (int rate in validRates)
        {
            if (rate >= required)
                return rate;
        }
        return 2400000; // Max practical RTL-SDR rate
    }

    // --- Private helpers ---

    /// <summary>
    /// Design a low-pass FIR filter using the windowed sinc method with a Blackman window.
    /// </summary>
    /// <param name="normalizedCutoff">Cutoff frequency / sample rate (0 to 0.5)</param>
    /// <param name="numTaps">Number of filter taps (should be odd)</param>
    internal static double[] DesignLowPass(double normalizedCutoff, int numTaps)
    {
        var h = new double[numTaps];
        int M = numTaps - 1;
        double sum = 0;

        for (int n = 0; n <= M; n++)
        {
            // Sinc function
            double x = n - M / 2.0;
            double sinc;
            if (Math.Abs(x) < 1e-10)
                sinc = 2.0 * normalizedCutoff;
            else
                sinc = Math.Sin(2.0 * Math.PI * normalizedCutoff * x) / (Math.PI * x);

            // Blackman window: good sidelobe rejection (~75 dB)
            double window = 0.42 - 0.5 * Math.Cos(2.0 * Math.PI * n / M)
                                  + 0.08 * Math.Cos(4.0 * Math.PI * n / M);

            h[n] = sinc * window;
            sum += h[n];
        }

        // Normalize for unity DC gain
        if (Math.Abs(sum) > 1e-10)
        {
            for (int i = 0; i < numTaps; i++)
                h[i] /= sum;
        }

        return h;
    }

    /// <summary>
    /// Find the best factor for splitting a decimation into two stages.
    /// Prefers factors near sqrt(total) for balanced computational cost.
    /// </summary>
    private static int FindBestFactor(int total)
    {
        int best = 1;
        int sqrtN = (int)Math.Sqrt(total);
        for (int f = sqrtN; f >= 2; f--)
        {
            if (total % f == 0)
            {
                best = f;
                break;
            }
        }
        // If no factor found near sqrt, try ascending
        if (best == 1)
        {
            for (int f = 2; f <= total / 2; f++)
            {
                if (total % f == 0)
                {
                    best = f;
                    break;
                }
            }
        }
        return best == 1 ? total : best; // Fallback to single stage
    }
}
