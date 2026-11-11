using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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
/// <remarks>
/// This runs once per channel on every wideband IQ sample (2.4 MSPS), so the per-input-sample
/// path is kept minimal: a lookup table for u8 conversion, locals for the DC blocker and NCO
/// state, and float delay lines laid out so each FIR output is one contiguous SIMD dot product.
/// Everything after the filters (metering, demod) runs at the 48 kHz output rate and stays in
/// double precision.
/// </remarks>
public class Channelizer
{
    private readonly int _inputRate;
    private readonly int _outputRate;
    private readonly double _offsetHz;
    private readonly bool _amMode;

    // u8 RTL-SDR sample -> [-1, 1]; shared by all channelizers.
    private static readonly double[] U8ToUnit = BuildU8Table();

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
    private readonly FirState _fir1;
    private int _decimCount1;

    // Stage 2 decimation
    private readonly int _decim2;
    private readonly double[] _filter2;
    private readonly FirState? _fir2;
    private int _decimCount2;

    // Channel filter: runs at the output rate on complex baseband and provides all of the
    // selectivity. Empty when the caller asks for no channel shaping.
    private readonly double[] _filter3;
    private readonly FirState? _fir3;

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
        _fir1 = new FirState(_filter1);

        if (_decim2 > 1)
        {
            // Stage 2: same argument one rate down — stop by outputRate - keep.
            double cutoff2 = 0.8 * (outputRate / 2.0);
            int taps2 = BlackmanTaps(intermediateRate, transitionHz: outputRate - keepHz - cutoff2, minimum: 21);
            _filter2 = DesignLowPass(cutoff2 / intermediateRate, taps2);
            _fir2 = new FirState(_filter2);
        }
        else
        {
            _filter2 = Array.Empty<double>();
        }

        // Stage 3: the actual channel filter, and the only one sized for selectivity. A 2 kHz
        // transition puts the adjacent 12.5 kHz channel well inside the stopband while leaving
        // the wanted signal untouched.
        if (channelCutoffHz > 0 && channelCutoffHz < outputRate / 2.0)
        {
            int taps3 = BlackmanTaps(outputRate, transitionHz: 2000, minimum: 31);
            _filter3 = DesignLowPass(channelCutoffHz / outputRate, taps3);
            _fir3 = new FirState(_filter3);
        }
        else
        {
            _filter3 = Array.Empty<double>();
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
    /// Safe to call concurrently on different instances with the same input buffer.
    /// </summary>
    /// <param name="iq">RTL-SDR output: unsigned 8-bit interleaved I,Q pairs</param>
    /// <param name="length">Number of bytes to process (must be even)</param>
    /// <param name="output">Pre-allocated buffer for s16le PCM output</param>
    /// <returns>Number of bytes written to output</returns>
    public int ProcessIQ(byte[] iq, int length, byte[] output)
    {
        int outIdx = 0;
        double[] lut = U8ToUnit;
        FirState fir1 = _fir1;
        int decim1 = _decim1;
        int decimCount1 = _decimCount1;

        // Per-input-sample state lives in locals for the loop and is written back at the end.
        double dcI = _dcI, dcQ = _dcQ, dcAlpha = _dcAlpha;
        double ncoRe = _ncoRe, ncoIm = _ncoIm;
        double stepRe = _ncoStepRe, stepIm = _ncoStepIm;
        int sinceNormalize = _ncoSinceNormalize;

        for (int i = 0; i + 1 < length; i += 2)
        {
            double rawI = lut[iq[i]];
            double rawQ = lut[iq[i + 1]];

            // Track and subtract the input DC offset. 127.5 is only the nominal centre; the
            // real one moves with gain and temperature, and whatever is left of it is the
            // dongle's centre spike.
            dcI += dcAlpha * (rawI - dcI);
            dcQ += dcAlpha * (rawQ - dcQ);
            rawI -= dcI;
            rawQ -= dcQ;

            // NCO frequency shift: complex multiply by the current phasor, then advance the
            // phasor by one fixed step. Identical to multiplying by e^(j*phase) each sample.
            fir1.Push((float)(rawI * ncoRe - rawQ * ncoIm), (float)(rawI * ncoIm + rawQ * ncoRe));

            double nextRe = ncoRe * stepRe - ncoIm * stepIm;
            ncoIm = ncoRe * stepIm + ncoIm * stepRe;
            ncoRe = nextRe;

            if (++sinceNormalize >= NcoNormalizeInterval)
            {
                sinceNormalize = 0;
                double mag = Math.Sqrt(ncoRe * ncoRe + ncoIm * ncoIm);
                if (mag > 1e-12) { ncoRe /= mag; ncoIm /= mag; }
            }

            if (++decimCount1 < decim1) continue;
            decimCount1 = 0;

            // Stage 1: anti-alias FIR output at the intermediate rate
            fir1.Compute(out float fI, out float fQ);

            // Stage 2 (if applicable): anti-alias FIR down to the output rate
            if (_fir2 != null)
            {
                _fir2.Push(fI, fQ);
                if (++_decimCount2 < _decim2) continue;
                _decimCount2 = 0;
                _fir2.Compute(out fI, out fQ);
            }

            // Stage 3: the channel filter. No decimation here — it runs at the output rate,
            // which is what makes a sharp filter affordable.
            if (_fir3 != null)
            {
                _fir3.Push(fI, fQ);
                _fir3.Compute(out fI, out fQ);
            }

            short pcm = Demodulate(fI, fQ);
            output[outIdx++] = (byte)(pcm & 0xFF);
            output[outIdx++] = (byte)((pcm >> 8) & 0xFF);
        }

        _decimCount1 = decimCount1;
        _dcI = dcI; _dcQ = dcQ;
        _ncoRe = ncoRe; _ncoIm = ncoIm;
        _ncoSinceNormalize = sinceNormalize;
        return outIdx;
    }

    /// <summary>
    /// Meter and demodulate one channel-filtered baseband sample into a clamped s16 PCM value.
    /// Runs at the output rate, so double precision costs nothing here.
    /// </summary>
    private short Demodulate(double fI, double fQ)
    {
        // Meter after the channel filter, so this reads in-channel power rather than
        // everything the decimation chain let through.
        _iqPowerAccum += fI * fI + fQ * fQ;
        _iqPowerCount++;

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
        return (short)(sample * 32000);
    }

    /// <summary>
    /// Reset internal state (NCO phase, filter delay lines, demod state).
    /// Call when switching to a new IQ stream segment.
    /// </summary>
    public void Reset()
    {
        _ncoRe = 1.0; _ncoIm = 0; _ncoSinceNormalize = 0;
        _dcI = 0; _dcQ = 0;
        _fir1.Reset(); _decimCount1 = 0;
        _fir2?.Reset(); _decimCount2 = 0;
        _fir3?.Reset();
        _prevI = 0; _prevQ = 0;
        _amDcEstimate = 0;
        _iqPowerAccum = 0; _phaseDiffAccum = 0; _iqPowerCount = 0;
        _signalPowerDb = -90.0; _frequencyOffsetHz = 0;
    }

    private static double[] BuildU8Table()
    {
        var t = new double[256];
        for (int b = 0; b < 256; b++)
            t[b] = (b - 127.5) * (1.0 / 127.5);
        return t;
    }

    /// <summary>
    /// Complex FIR filter state. Samples are stored interleaved (I, Q) in a ring buffer that is
    /// written twice (at p and p + taps), so the most recent <c>taps</c> samples are always one
    /// contiguous run and the filter output is a single dot product with no wraparound.
    /// Coefficients are stored time-reversed and duplicated (h, h) to line up with (I, Q) lanes.
    /// </summary>
    private sealed class FirState
    {
        private readonly int _taps;
        private readonly float[] _coef;   // 2 * taps
        private readonly float[] _delay;  // 4 * taps (2 * taps complex samples)
        private int _pos;                 // next write slot, in complex samples [0, taps)

        public FirState(double[] filter)
        {
            _taps = filter.Length;
            _coef = new float[2 * _taps];
            for (int j = 0; j < _taps; j++)
            {
                // Window slot j (0 = oldest) pairs with h[taps - 1 - j].
                float h = (float)filter[_taps - 1 - j];
                _coef[2 * j] = h;
                _coef[2 * j + 1] = h;
            }
            _delay = new float[4 * _taps];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(float i, float q)
        {
            int w = _pos * 2;
            int mirror = w + 2 * _taps;
            float[] d = _delay;
            d[w] = i; d[w + 1] = q;
            d[mirror] = i; d[mirror + 1] = q;
            if (++_pos == _taps) _pos = 0;
        }

        public void Compute(out float outI, out float outQ)
        {
            // Window = complex slots [_pos, _pos + taps), oldest first. Indices stay within
            // _delay because _pos < taps and the buffer holds 2 * taps complex samples.
            int len = _coef.Length;
            ref float x = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_delay), _pos * 2);
            ref float h = ref MemoryMarshal.GetArrayDataReference(_coef);
            float sI = 0, sQ = 0;
            int k = 0;

            if (Vector128.IsHardwareAccelerated && len >= 8)
            {
                // Lanes alternate I, Q, I, Q (start offset is always even).
                var acc0 = Vector128<float>.Zero;
                var acc1 = Vector128<float>.Zero;
                for (; k <= len - 8; k += 8)
                {
                    acc0 = Vector128.MultiplyAddEstimate(
                        Vector128.LoadUnsafe(ref x, (nuint)k), Vector128.LoadUnsafe(ref h, (nuint)k), acc0);
                    acc1 = Vector128.MultiplyAddEstimate(
                        Vector128.LoadUnsafe(ref x, (nuint)(k + 4)), Vector128.LoadUnsafe(ref h, (nuint)(k + 4)), acc1);
                }
                if (k <= len - 4)
                {
                    acc0 = Vector128.MultiplyAddEstimate(
                        Vector128.LoadUnsafe(ref x, (nuint)k), Vector128.LoadUnsafe(ref h, (nuint)k), acc0);
                    k += 4;
                }
                var acc = acc0 + acc1;
                sI = acc.GetElement(0) + acc.GetElement(2);
                sQ = acc.GetElement(1) + acc.GetElement(3);
            }

            for (; k < len; k += 2)
            {
                sI += Unsafe.Add(ref x, k) * Unsafe.Add(ref h, k);
                sQ += Unsafe.Add(ref x, k + 1) * Unsafe.Add(ref h, k + 1);
            }

            outI = sI;
            outQ = sQ;
        }

        public void Reset()
        {
            Array.Clear(_delay);
            _pos = 0;
        }
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
