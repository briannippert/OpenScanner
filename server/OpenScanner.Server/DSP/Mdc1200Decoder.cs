namespace OpenScanner.Server.DSP;

/// <summary>
/// Decodes MDC1200 signaling from 48kHz s16le PCM audio.
/// MDC1200 uses 1200-baud AFSK with 1200 Hz mark (logical 1) and 1800 Hz space (logical 0).
/// A packet consists of a preamble of alternating bits followed by a 40-bit codeword:
/// opcode (8 bits) + argument (8 bits) + unit ID (16 bits) + CRC (8 bits).
/// CRC is the one's complement of the byte sum of the preceding four bytes.
/// </summary>
public class Mdc1200Decoder
{
    private const int SampleRate = 48000;
    private const int BaudRate = 1200;
    private const int SamplesPerBit = SampleRate / BaudRate; // 40
    private const double MarkFreq = 1200.0;
    private const double SpaceFreq = 1800.0;

    // Minimum consecutive alternating bits to consider a valid preamble
    private const int MinPreambleBits = 24;

    // Maximum bits to read after preamble before giving up
    private const int MaxDataSearchBits = 80;

    // Cooldown after successful decode (in bits) to prevent duplicate detections
    private const int CooldownBits = BaudRate / 2; // 600 bits = 500ms

    // Known MDC1200 opcodes
    public const byte OpPttIdPre = 0x01;
    public const byte OpPttIdPost = 0x00;
    public const byte OpEmergency = 0x11;
    public const byte OpEmergencyAck = 0x35;
    public const byte OpCallAlert = 0x63;
    public const byte OpRadioCheck = 0x47;
    public const byte OpStatusRequest = 0x23;
    public const byte OpMessageRequest = 0x46;

    // Goertzel coefficients (precomputed)
    private readonly double _markCoeff;
    private readonly double _spaceCoeff;

    // Sample accumulation for bit-rate processing
    private readonly double[] _bitWindow = new double[SamplesPerBit];
    private int _windowPos;

    // State machine
    private enum State { Scanning, Preamble, Data }
    private State _state = State.Scanning;

    // Preamble tracking
    private int _alternatingCount;
    private int _lastBit = -1;

    // Data collection after preamble
    private ulong _dataBits;
    private int _dataBitCount;
    private int _dataSearchCount;

    // Cooldown counter
    private int _cooldownRemaining;

    /// <summary>
    /// Fires when a valid MDC1200 packet is decoded.
    /// Parameters: unitId (0-65535), opcode, argument.
    /// </summary>
    public event Action<int, byte, byte>? OnPacket;

    public Mdc1200Decoder()
    {
        // Goertzel: k = N * f / fs, coeff = 2 * cos(2 * pi * k / N)
        double kMark = (double)SamplesPerBit * MarkFreq / SampleRate;
        double kSpace = (double)SamplesPerBit * SpaceFreq / SampleRate;
        _markCoeff = 2.0 * Math.Cos(2.0 * Math.PI * kMark / SamplesPerBit);
        _spaceCoeff = 2.0 * Math.Cos(2.0 * Math.PI * kSpace / SamplesPerBit);
    }

    /// <summary>
    /// Process a chunk of 48kHz s16le PCM audio looking for MDC1200 bursts.
    /// Call this continuously with channelizer output (before squelch gating)
    /// so that MDC bursts at the start/end of transmissions are not missed.
    /// </summary>
    public void Process(byte[] pcm, int length)
    {
        for (int i = 0; i + 1 < length; i += 2)
        {
            short sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            _bitWindow[_windowPos++] = sample / 32768.0;

            if (_windowPos >= SamplesPerBit)
            {
                _windowPos = 0;
                int bit = DemodulateOneBit();
                ProcessBit(bit);
            }
        }
    }

    /// <summary>
    /// Demodulate one bit using Goertzel at mark and space frequencies.
    /// Returns 1 for mark (1200 Hz) or 0 for space (1800 Hz).
    /// </summary>
    private int DemodulateOneBit()
    {
        // Goertzel for mark (1200 Hz)
        double ms1 = 0, ms2 = 0;
        for (int i = 0; i < SamplesPerBit; i++)
        {
            double s = _bitWindow[i] + _markCoeff * ms1 - ms2;
            ms2 = ms1;
            ms1 = s;
        }
        double markPower = ms1 * ms1 + ms2 * ms2 - _markCoeff * ms1 * ms2;

        // Goertzel for space (1800 Hz)
        double ss1 = 0, ss2 = 0;
        for (int i = 0; i < SamplesPerBit; i++)
        {
            double s = _bitWindow[i] + _spaceCoeff * ss1 - ss2;
            ss2 = ss1;
            ss1 = s;
        }
        double spacePower = ss1 * ss1 + ss2 * ss2 - _spaceCoeff * ss1 * ss2;

        return markPower > spacePower ? 1 : 0;
    }

    /// <summary>
    /// Feed one demodulated bit into the state machine.
    /// </summary>
    private void ProcessBit(int bit)
    {
        if (_cooldownRemaining > 0)
        {
            _cooldownRemaining--;
            _lastBit = -1;
            _alternatingCount = 0;
            _state = State.Scanning;
            return;
        }

        switch (_state)
        {
            case State.Scanning:
                // Look for alternating bits to detect preamble
                if (_lastBit >= 0 && bit != _lastBit)
                {
                    _alternatingCount++;
                    if (_alternatingCount >= MinPreambleBits)
                        _state = State.Preamble;
                }
                else if (_lastBit >= 0 && bit == _lastBit)
                {
                    _alternatingCount = 0;
                }
                _lastBit = bit;
                break;

            case State.Preamble:
                // Continue counting alternating bits; transition to Data
                // when the alternating pattern breaks (two consecutive same-value bits)
                if (bit != _lastBit)
                {
                    _alternatingCount++;
                    _lastBit = bit;
                }
                else
                {
                    // Pattern broken -- data starts now.
                    // The current bit is the first data bit.
                    _state = State.Data;
                    _dataBits = (ulong)bit;
                    _dataBitCount = 1;
                    _dataSearchCount = 1;
                    _lastBit = bit;
                }
                break;

            case State.Data:
                _dataBits = (_dataBits << 1) | (ulong)(uint)bit;
                _dataBitCount++;
                _dataSearchCount++;
                _lastBit = bit;

                // Once we have 40+ bits, try to decode on every new bit
                if (_dataBitCount >= 40)
                {
                    if (TryDecode(_dataBits) || TryDecode(InvertBits(_dataBits, 40)))
                    {
                        ResetState();
                        return;
                    }
                }

                // Give up after too many bits without a valid CRC
                if (_dataSearchCount >= MaxDataSearchBits)
                {
                    ResetState();
                }
                break;
        }
    }

    /// <summary>
    /// Attempt to extract and validate a 40-bit MDC1200 codeword from the
    /// least-significant 40 bits of the shift register.
    /// Layout (MSB first): op[7:0] arg[7:0] idHi[7:0] idLo[7:0] crc[7:0]
    /// CRC = ~(op + arg + idHi + idLo) AND 0xFF
    /// </summary>
    private bool TryDecode(ulong bits)
    {
        ulong data = bits & 0xFFFFFFFFFF; // 40 bits
        byte crc  = (byte)(data & 0xFF); data >>= 8;
        byte idLo = (byte)(data & 0xFF); data >>= 8;
        byte idHi = (byte)(data & 0xFF); data >>= 8;
        byte arg  = (byte)(data & 0xFF); data >>= 8;
        byte op   = (byte)(data & 0xFF);

        byte expected = (byte)(~(op + arg + idHi + idLo) & 0xFF);
        if (crc != expected) return false;

        // Validate opcode to reduce false positives
        if (!IsKnownOpcode(op)) return false;

        int unitId = (idHi << 8) | idLo;
        _cooldownRemaining = CooldownBits;
        OnPacket?.Invoke(unitId, op, arg);
        return true;
    }

    private static bool IsKnownOpcode(byte op)
    {
        return op == OpPttIdPre || op == OpPttIdPost || op == OpEmergency
            || op == OpEmergencyAck || op == OpCallAlert || op == OpRadioCheck
            || op == OpStatusRequest || op == OpMessageRequest;
    }

    private static ulong InvertBits(ulong bits, int count)
    {
        ulong mask = (1UL << count) - 1;
        return (~bits) & mask;
    }

    private void ResetState()
    {
        _state = State.Scanning;
        _alternatingCount = 0;
        _lastBit = -1;
        _dataBitCount = 0;
        _dataSearchCount = 0;
        _dataBits = 0;
    }

    /// <summary>Reset all decoder state including cooldown.</summary>
    public void Reset()
    {
        ResetState();
        _windowPos = 0;
        _cooldownRemaining = 0;
    }

    /// <summary>
    /// Describe an opcode as a human-readable string for logging.
    /// </summary>
    public static string DescribeOpcode(byte opcode)
    {
        return opcode switch
        {
            OpPttIdPre => "PTT-ID (pre)",
            OpPttIdPost => "PTT-ID (post)",
            OpEmergency => "EMERGENCY",
            OpEmergencyAck => "Emergency Ack",
            OpCallAlert => "Call Alert",
            OpRadioCheck => "Radio Check",
            OpStatusRequest => "Status Request",
            OpMessageRequest => "Message Request",
            _ => $"Op 0x{opcode:X2}"
        };
    }
}
