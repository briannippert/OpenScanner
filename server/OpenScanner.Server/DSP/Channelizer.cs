using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OpenScanner.Server.DSP;

/// <summary>
/// Isolates a single narrowband channel from a wideband IQ stream.
/// Pipeline: NCO frequency shift -> two-stage FIR decimation -> FM/AM demodulation.
/// Produces 48 kHz s16le PCM audio suitable for feeding to decoders.
/// </summary>
/// <remarks>
/// This runs once per channel on every wideband IQ sample (2.4 MSPS), so the per-input-sample
/// path is kept minimal: a lookup table for u8 -> float, a phasor rotation instead of
/// trig calls for the NCO, and float delay lines laid out so each FIR output is one
/// contiguous SIMD dot product. Everything after decimation (metering, demod) runs at the
/// 48 kHz output rate and stays in double precision.
/// </remarks>
public class Channelizer
{
    private readonly int _inputRate;
    private readonly int _outputRate;
    private readonly double _offsetHz;
    private readonly bool _amMode;

    // u8 RTL-SDR sample -> float in [-1, 1]; shared by all channelizers.
    private static readonly float[] U8ToFloat = BuildU8Table();

    // NCO (Numerically Controlled Oscillator). The phasor (cos, sin) is advanced by complex
    // multiplication each sample and re-synced exactly from _ncoPhase every NcoBlock samples,
    // so rounding drift never accumulates and output is independent of chunk boundaries.
    private const int NcoBlock = 1024;
    private double _ncoPhase;           // phase at the start of the next resync block
    private readonly double _ncoPhaseInc;
    private readonly double _ncoBlockInc;
    private readonly double _ncoRotCos;
    private readonly double _ncoRotSin;
    private double _ncoCos;
    private double _ncoSin;
    private int _ncoBlockLeft;

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
        _ncoBlockInc = Math.IEEERemainder(_ncoPhaseInc * NcoBlock, 2.0 * Math.PI);
        _ncoRotCos = Math.Cos(_ncoPhaseInc);
        _ncoRotSin = Math.Sin(_ncoPhaseInc);

        // Design decimation filters using windowed sinc method.
        // Cutoff at 80% of the output Nyquist to provide transition band.
        double cutoff1Normalized = 0.8 * (intermediateRate / 2.0) / inputRate;
        int taps1 = Math.Max(4 * _decim1, 32) | 1; // Ensure odd number of taps
        _filter1 = DesignLowPass(cutoff1Normalized, taps1);
        _fir1 = new FirState(_filter1);

        if (_decim2 > 1)
        {
            double cutoff2Normalized = 0.8 * (outputRate / 2.0) / intermediateRate;
            int taps2 = Math.Max(4 * _decim2, 16) | 1;
            _filter2 = DesignLowPass(cutoff2Normalized, taps2);
            _fir2 = new FirState(_filter2);
        }
        else
        {
            _filter2 = Array.Empty<double>();
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
    /// Safe to call concurrently on different instances with the same input buffer.
    /// </summary>
    /// <param name="iq">RTL-SDR output: unsigned 8-bit interleaved I,Q pairs</param>
    /// <param name="length">Number of bytes to process (must be even)</param>
    /// <param name="output">Pre-allocated buffer for s16le PCM output</param>
    /// <returns>Number of bytes written to output</returns>
    public int ProcessIQ(byte[] iq, int length, byte[] output)
    {
        int outIdx = 0;
        float[] lut = U8ToFloat;
        FirState fir1 = _fir1;
        int decim1 = _decim1;
        int decimCount1 = _decimCount1;

        double c = _ncoCos, s = _ncoSin;
        double rotC = _ncoRotCos, rotS = _ncoRotSin;
        int blockLeft = _ncoBlockLeft;

        for (int i = 0; i + 1 < length; i += 2)
        {
            if (blockLeft == 0)
            {
                // Exact resync of the phasor; bounds rotation drift to NcoBlock steps.
                c = Math.Cos(_ncoPhase);
                s = Math.Sin(_ncoPhase);
                _ncoPhase = Math.IEEERemainder(_ncoPhase + _ncoBlockInc, 2.0 * Math.PI);
                blockLeft = NcoBlock;
            }
            blockLeft--;

            float rawI = lut[iq[i]];
            float rawQ = lut[iq[i + 1]];

            // NCO frequency shift (complex multiply by e^(j*phase)), then advance the phasor.
            fir1.Push((float)(rawI * c - rawQ * s), (float)(rawI * s + rawQ * c));
            double nc = c * rotC - s * rotS;
            s = c * rotS + s * rotC;
            c = nc;

            if (++decimCount1 < decim1) continue;
            decimCount1 = 0;

            // Stage 1: FIR output at the intermediate rate
            fir1.Compute(out float fI1, out float fQ1);

            // Stage 2 (if applicable)
            double fI, fQ;
            if (_fir2 != null)
            {
                _fir2.Push(fI1, fQ1);
                if (++_decimCount2 < _decim2) continue;
                _decimCount2 = 0;

                _fir2.Compute(out float fI2, out float fQ2);
                fI = fI2;
                fQ = fQ2;
            }
            else
            {
                fI = fI1;
                fQ = fQ1;
            }

            short pcm = Demodulate(fI, fQ);
            output[outIdx++] = (byte)(pcm & 0xFF);
            output[outIdx++] = (byte)((pcm >> 8) & 0xFF);
        }

        _decimCount1 = decimCount1;
        _ncoCos = c;
        _ncoSin = s;
        _ncoBlockLeft = blockLeft;
        return outIdx;
    }

    /// <summary>
    /// Meter and demodulate one decimated IQ sample into a clamped s16 PCM value.
    /// Runs at the output rate, so double precision costs nothing here.
    /// </summary>
    private short Demodulate(double fI, double fQ)
    {
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

        double sample;
        if (_amMode)
        {
            double envelope = Math.Sqrt(mag2);
            // Remove DC with single-pole IIR highpass
            _amDcEstimate = AmDcAlpha * _amDcEstimate + (1.0 - AmDcAlpha) * envelope;
            sample = (envelope - _amDcEstimate) * 4.0; // Gain to fill range
        }
        else
        {
            // FM quadrature discriminator: angle between consecutive IQ samples
            double dot = fI * _prevI + fQ * _prevQ;
            double cross = fQ * _prevI - fI * _prevQ;
            sample = Math.Atan2(cross, dot) * _fmScale;
        }
        _prevI = fI;
        _prevQ = fQ;

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
        _ncoPhase = 0; _ncoBlockLeft = 0; _ncoCos = 1; _ncoSin = 0;
        _fir1.Reset(); _decimCount1 = 0;
        _fir2?.Reset(); _decimCount2 = 0;
        _prevI = 0; _prevQ = 0;
        _amDcEstimate = 0;
        _iqPowerAccum = 0; _iqPowerCount = 0; _signalPowerDb = -90.0;
    }

    private static float[] BuildU8Table()
    {
        var t = new float[256];
        for (int b = 0; b < 256; b++)
            t[b] = (float)((b - 127.5) * (1.0 / 127.5));
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
            // Window = complex slots [_pos, _pos + taps), oldest first.
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
