namespace OpenScanner.Server.DSP;

/// <summary>
/// Isolates a single narrowband channel from a wideband IQ stream.
/// Pipeline: DC block -> NCO frequency shift -> two-stage FIR decimation -> channel filter
/// -> FM/AM demodulation. Produces 48 kHz s16le PCM audio suitable for feeding to decoders.
///
/// The three filters have distinct jobs and are sized independently:
/// stages 1 and 2 exist only to prevent aliasing as the rate comes down, while the channel
/// filter at the output rate provides all of the selectivity. Sizing the decimation filters by
/// their decimation factor (as this did before 2026) is unrelated to the selectivity a channel
/// actually needs, and left a 12.5 kHz-spaced neighbour sitting inside the passband.
/// </summary>
public class Channelizer
{
    private readonly int _inputRate;
    private readonly int _outputRate;
    private readonly double _offsetHz;
    private readonly bool _amMode;

    // NCO, as a recursive complex rotator. Multiplying by a fixed unit phasor each sample costs
    // one complex multiply instead of a Math.Cos/Math.Sin pair — at 2.4 Msps that pair was
    // 4.8M transcendental calls per second per channel and dominated the whole pipeline.
    private double _ncoRe = 1.0;
    private double _ncoIm;
    private readonly double _ncoStepRe;
    private readonly double _ncoStepIm;
    private int _ncoSinceNormalize;
    /// <summary>Repeated complex multiplies let the rotator's magnitude creep; renormalize periodically.</summary>
    private const int NcoNormalizeInterval = 1024;

    // Input DC removal. RTL-SDR dongles put a large spike at the tuner centre; without this it
    // lands inside whichever channel sits at the centre frequency.
    private double _dcI;
    private double _dcQ;
    private readonly double _dcAlpha;
    /// <summary>Corner frequency of the input DC blocker. Low enough to touch nothing but the spike.</summary>
    private const double DcBlockerCornerHz = 500.0;

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

    // Channel filter: runs at the output rate on complex baseband and provides all of the
    // selectivity. Empty when the caller asks for no channel shaping.
    private readonly double[] _filter3;
    private readonly double[] _delayI3;
    private readonly double[] _delayQ3;
    private int _writePtr3;

    // FM demodulator state (previous IQ sample for phase differencing)
    private double _prevI;
    private double _prevQ;

    // AM DC removal (simple single-pole IIR highpass)
    private double _amDcEstimate;
    private const double AmDcAlpha = 0.995;

    // FM scaling: convert phase-diff to normalized audio
    private readonly double _fmScale;

    // Signal metering, accumulated over the same window: in-channel power and discriminator DC.
    private double _iqPowerAccum;
    private double _phaseDiffAccum;
    private int _iqPowerCount;
    private double _signalPowerDb = -90.0;
    private double _frequencyOffsetHz;
    private const int IqPowerWindow = 4800; // ~100ms at 48kHz output rate

    /// <summary>
    /// Latest measured signal power in dB, taken on the complex baseband after the channel
    /// filter and before demodulation. Measuring it post-filter makes it genuine in-channel
    /// power rather than everything the decimation chain happened to let through, which is
    /// what makes it usable as a squelch metric.
    /// Updated every ~100ms. Noise floor typically sits around -30 to -40 dB; a carrier
    /// reads -5 to 0 dB.
    /// </summary>
    public double SignalPowerDb => _signalPowerDb;

    /// <summary>
    /// Mean FM discriminator output over the last measurement window, in Hz. For a signal
    /// centred in the passband this is zero; a non-zero reading is the frequency error between
    /// the transmitter and where we tuned, which over a known-accurate reference signal is the
    /// dongle's crystal error. Meaningless in AM mode or with no signal present.
    /// </summary>
    public double FrequencyOffsetHz => _frequencyOffsetHz;

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
    /// <param name="channelCutoffHz">
    /// One-sided bandwidth of the wanted channel, in Hz — 6250 for a 12.5 kHz channel, 12500 for
    /// a 25 kHz one. Zero disables the channel filter, leaving only the anti-alias stages
    /// (the pre-2026 behaviour).
    /// </param>
    /// <param name="deviationHz">
    /// Peak frequency deviation the transmitter uses, which sets the audio scaling. 5000 suits
    /// analog voice; P25 C4FM peaks at 1800 and DMR 4FSK at 1944, and scaling those as if they
    /// were 5 kHz leaves the decoder ~11 dB of unused headroom.
    /// </param>
    public Channelizer(
        int inputRate,
        int outputRate,
        double offsetHz,
        bool amMode = false,
        double channelCutoffHz = 0,
        double deviationHz = 5000.0)
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

        // NCO as a fixed unit phasor, applied by repeated complex multiply.
        // Negative angle for downconversion, matching the old phase increment.
        double ncoStep = -2.0 * Math.PI * offsetHz / inputRate;
        _ncoStepRe = Math.Cos(ncoStep);
        _ncoStepIm = Math.Sin(ncoStep);

        _dcAlpha = 2.0 * Math.PI * DcBlockerCornerHz / inputRate;

        // Everything the channel filter will ultimately keep. Used to size the anti-alias
        // stages: they only have to stop what would fold *into this band*, not everything
        // above their own cutoff, which is what keeps them cheap.
        double keepHz = channelCutoffHz > 0 ? channelCutoffHz : outputRate / 2.0;

        // Stage 1: decimating to intermediateRate folds content near multiples of
        // intermediateRate down onto us, so the stopband has to start at intermediateRate - keep.
        double cutoff1 = 0.8 * (intermediateRate / 2.0);
        int taps1 = BlackmanTaps(inputRate, transitionHz: intermediateRate - keepHz - cutoff1, minimum: 33);
        _filter1 = DesignLowPass(cutoff1 / inputRate, taps1);
        _delayI1 = new double[taps1];
        _delayQ1 = new double[taps1];

        if (_decim2 > 1)
        {
            // Stage 2: same argument one rate down — stop by outputRate - keep.
            double cutoff2 = 0.8 * (outputRate / 2.0);
            int taps2 = BlackmanTaps(intermediateRate, transitionHz: outputRate - keepHz - cutoff2, minimum: 21);
            _filter2 = DesignLowPass(cutoff2 / intermediateRate, taps2);
            _delayI2 = new double[taps2];
            _delayQ2 = new double[taps2];
        }
        else
        {
            _filter2 = Array.Empty<double>();
            _delayI2 = Array.Empty<double>();
            _delayQ2 = Array.Empty<double>();
        }

        // Stage 3: the actual channel filter, and the only one sized for selectivity. A 2 kHz
        // transition puts the adjacent 12.5 kHz channel well inside the stopband while leaving
        // the wanted signal untouched.
        if (channelCutoffHz > 0 && channelCutoffHz < outputRate / 2.0)
        {
            int taps3 = BlackmanTaps(outputRate, transitionHz: 2000, minimum: 31);
            _filter3 = DesignLowPass(channelCutoffHz / outputRate, taps3);
            _delayI3 = new double[taps3];
            _delayQ3 = new double[taps3];
        }
        else
        {
            _filter3 = Array.Empty<double>();
            _delayI3 = Array.Empty<double>();
            _delayQ3 = Array.Empty<double>();
        }

        // FM gain: scale atan2 output (radians) so peak deviation maps to ~80% of s16le range.
        // atan2 output at peak deviation is 2*pi*deviation/outputRate radians.
        _fmScale = 0.8 * outputRate / (2.0 * Math.PI * deviationHz);
    }

    /// <summary>
    /// Tap count for a Blackman-windowed FIR with the given transition width. The Blackman
    /// window's transition is about 5.5/N of the sample rate for ~74 dB of stopband, so the
    /// count follows from how sharp the filter has to be — not, as this used to assume, from
    /// the decimation factor, which says nothing about the required selectivity.
    /// </summary>
    internal static int BlackmanTaps(double sampleRate, double transitionHz, int minimum)
    {
        if (transitionHz <= 0) return minimum | 1;
        int taps = (int)Math.Ceiling(5.5 * sampleRate / transitionHz);
        return Math.Max(taps, minimum) | 1; // odd, for a symmetric linear-phase filter
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

            // Track and subtract the input DC offset. 127.5 is only the nominal centre; the
            // real one moves with gain and temperature, and whatever is left of it is the
            // dongle's centre spike.
            _dcI += _dcAlpha * (rawI - _dcI);
            _dcQ += _dcAlpha * (rawQ - _dcQ);
            rawI -= _dcI;
            rawQ -= _dcQ;

            // NCO frequency shift: complex multiply by the current phasor, then advance the
            // phasor by one fixed step. Identical to multiplying by e^(j*phase) each sample.
            double sI = rawI * _ncoRe - rawQ * _ncoIm;
            double sQ = rawI * _ncoIm + rawQ * _ncoRe;

            double nextRe = _ncoRe * _ncoStepRe - _ncoIm * _ncoStepIm;
            _ncoIm = _ncoRe * _ncoStepIm + _ncoIm * _ncoStepRe;
            _ncoRe = nextRe;

            if (++_ncoSinceNormalize >= NcoNormalizeInterval)
            {
                _ncoSinceNormalize = 0;
                double mag = Math.Sqrt(_ncoRe * _ncoRe + _ncoIm * _ncoIm);
                if (mag > 1e-12) { _ncoRe /= mag; _ncoIm /= mag; }
            }

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

            // Stage 3: the channel filter. No decimation here — it runs at the output rate,
            // which is what makes a sharp filter affordable.
            if (_filter3.Length > 0)
            {
                _delayI3[_writePtr3] = fI;
                _delayQ3[_writePtr3] = fQ;
                _writePtr3 = (_writePtr3 + 1) % _filter3.Length;

                fI = 0; fQ = 0;
                int rp3 = _writePtr3;
                for (int k = 0; k < _filter3.Length; k++)
                {
                    rp3--;
                    if (rp3 < 0) rp3 += _filter3.Length;
                    fI += _filter3[k] * _delayI3[rp3];
                    fQ += _filter3[k] * _delayQ3[rp3];
                }
            }

            // Meter after the channel filter, so this reads in-channel power rather than
            // everything the decimation chain let through.
            _iqPowerAccum += fI * fI + fQ * fQ;
            _iqPowerCount++;

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
                // Its mean is the carrier's offset from where we tuned; voice modulation
                // averages out over the window, leaving the frequency error behind.
                _phaseDiffAccum += phaseD;
                sample = phaseD * _fmScale;
            }
            _prevI = fI;
            _prevQ = fQ;

            if (_iqPowerCount >= IqPowerWindow)
            {
                double rmsMag = Math.Sqrt(_iqPowerAccum / _iqPowerCount);
                _signalPowerDb = 20.0 * Math.Log10(rmsMag + 1e-12);
                _frequencyOffsetHz = _phaseDiffAccum / _iqPowerCount * _outputRate / (2.0 * Math.PI);
                _iqPowerAccum = 0;
                _phaseDiffAccum = 0;
                _iqPowerCount = 0;
            }

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
        _ncoRe = 1.0; _ncoIm = 0; _ncoSinceNormalize = 0;
        _dcI = 0; _dcQ = 0;
        Array.Clear(_delayI1); Array.Clear(_delayQ1); _writePtr1 = 0; _decimCount1 = 0;
        Array.Clear(_delayI2); Array.Clear(_delayQ2); _writePtr2 = 0; _decimCount2 = 0;
        Array.Clear(_delayI3); Array.Clear(_delayQ3); _writePtr3 = 0;
        _prevI = 0; _prevQ = 0;
        _amDcEstimate = 0;
        _iqPowerAccum = 0; _phaseDiffAccum = 0; _iqPowerCount = 0;
        _signalPowerDb = -90.0; _frequencyOffsetHz = 0;
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
