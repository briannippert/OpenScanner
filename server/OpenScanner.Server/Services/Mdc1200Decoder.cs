using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

/// <summary>
/// Native MDC1200 (Motorola Data Communications) decoder. Consumes mono 16-bit
/// PCM audio and recovers 1200-baud MSK data bursts (PTT ID, emergency, etc.).
///
/// This is an independent implementation written from the public MDC1200 protocol
/// facts (sync word, CRC-16-CCITT, 7x16 interleave, 112-bit frame) — it is not a
/// port of any GPL-licensed source, so it stays compatible with this project's
/// MIT license.
///
/// The demodulator samples the sign of the audio at the 1200-baud bit clock across
/// several timing phases (the XOR-precoded MSK burst is designed so the polarity at
/// bit centres reproduces the on-air bit stream), searches for the sync word, then
/// collects, de-interleaves and CRC-checks the frame.
/// </summary>
public class Mdc1200Decoder
{
    private readonly ILogger<Mdc1200Decoder> _logger;

    // Number of parallel bit-timing hypotheses. More phases = more robust bit-clock
    // recovery at the cost of CPU. The reference decoders use a similar small bank.
    private const int PhaseCount = 6;

    // Sync word: high byte 0x07 followed by the 32-bit low word 0x092a446f (40 bits total).
    private const uint SyncHigh = 0x07;
    private const uint SyncLow = 0x092a446f;

    // Maximum Hamming distance from the 40-bit sync word to accept a match.
    private const int SyncThreshold = 5;

    // 32-bit phase accumulator increment per input sample for a 1200 Hz bit clock at 48 kHz.
    // The accumulator wraps once per bit period (2^32 / Incr48k == 40 samples == 48000/1200).
    private const uint Incr48k = 107374182;

    private readonly uint _increment;
    private readonly PhaseSlot[] _slots = new PhaseSlot[PhaseCount];

    /// <summary>Raised when a valid MDC1200 frame is decoded (CRC verified).</summary>
    public event Action<Mdc1200Packet>? OnPacket;

    public Mdc1200Decoder(ILogger<Mdc1200Decoder> logger, int sampleRate = 48000)
    {
        _logger = logger;
        _increment = sampleRate == 48000
            ? Incr48k
            : (uint)(1200L * 2 * (0x80000000L / sampleRate));

        for (int i = 0; i < PhaseCount; i++)
        {
            _slots[i] = new PhaseSlot
            {
                // Stagger the initial phase of each slot so their bit instants fall at
                // different points relative to the incoming symbol timing.
                Accumulator = (uint)(i * (0x80000000L / PhaseCount) * 2),
                State = SlotState.Hunting
            };
        }
    }

    /// <summary>
    /// Processes a chunk of mono 16-bit little-endian PCM audio.
    /// </summary>
    public void ProcessAudio(byte[] pcmData)
    {
        int sampleCount = pcmData.Length / 2;
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
            AdvanceSample(sample);
        }
    }

    private void AdvanceSample(short sample)
    {
        bool positive = sample > 0;

        foreach (var slot in _slots)
        {
            uint previous = slot.Accumulator;
            slot.Accumulator += _increment;
            // Wrap of the phase accumulator marks a bit-sampling instant.
            if (slot.Accumulator >= previous)
                continue;

            bool bit = positive;
            if (slot.Inverted)
                bit = !bit;

            ShiftBit(slot, bit);
        }
    }

    private void ShiftBit(PhaseSlot slot, bool bit)
    {
        switch (slot.State)
        {
            case SlotState.Hunting:
                // Roll the incoming bit into the 40-bit sync register (8 high + 32 low).
                slot.SyncHigh = ((slot.SyncHigh << 1) | (slot.SyncLow >> 31)) & 0xFF;
                slot.SyncLow = (slot.SyncLow << 1) | (bit ? 1u : 0u);

                int distance = PopCount((SyncHigh ^ slot.SyncHigh) & 0xFF)
                             + PopCount(SyncLow ^ slot.SyncLow);

                if (distance <= SyncThreshold)
                {
                    BeginCollecting(slot);
                }
                else if (distance >= 40 - SyncThreshold)
                {
                    // Matched the inverted sync word: the stream polarity is flipped.
                    slot.Inverted = !slot.Inverted;
                    BeginCollecting(slot);
                }
                break;

            case SlotState.Collecting:
                slot.Bits[slot.BitCount++] = bit;
                if (slot.BitCount >= 112)
                {
                    TryDecodeFrame(slot);
                    slot.State = SlotState.Hunting;
                }
                break;
        }
    }

    private static void BeginCollecting(PhaseSlot slot)
    {
        slot.State = SlotState.Collecting;
        slot.BitCount = 0;
    }

    private void TryDecodeFrame(PhaseSlot slot)
    {
        // De-interleave the 112 collected bits (7 rows x 16 columns) into 14 bytes.
        var linear = new bool[112];
        int idx = 0;
        for (int col = 0; col < 16; col++)
        {
            for (int row = 0; row < 7; row++)
            {
                linear[idx++] = slot.Bits[row * 16 + col];
            }
        }

        var data = new byte[14];
        for (int b = 0; b < 14; b++)
        {
            byte value = 0;
            for (int bitPos = 0; bitPos < 8; bitPos++)
            {
                if (linear[b * 8 + bitPos])
                    value |= (byte)(1 << bitPos);
            }
            data[b] = value;
        }

        ushort computed = Crc(data, 4);
        ushort received = (ushort)(data[4] | (data[5] << 8));
        if (computed != received)
            return;

        int op = data[0];
        int arg = data[1];
        int unitId = (data[2] << 8) | data[3];

        var packet = BuildPacket(op, arg, unitId);
        _logger.LogInformation(
            "MDC1200 decoded: unit={UnitId:X4} op={Op:X2} arg={Arg:X2} ({Desc})",
            unitId, op, arg, packet.Description);
        OnPacket?.Invoke(packet);
    }

    /// <summary>
    /// Maps a decoded op/arg to a friendly description. MDC opcode meanings vary by
    /// fleet; only widely-documented ones are named, the rest fall back to raw hex.
    /// </summary>
    private static Mdc1200Packet BuildPacket(int op, int arg, int unitId)
    {
        bool emergency = op == 0x00 && arg == 0x03;
        string description = (op, arg) switch
        {
            (0x01, _) => "PTT ID",
            (0x00, 0x03) => "Emergency",
            (0x22, _) => "Radio Check",
            (0x03, _) => "Message",
            (0x12, _) => "Status Request",
            _ => $"MDC op={op:X2} arg={arg:X2}"
        };

        return new Mdc1200Packet
        {
            UnitId = unitId,
            Op = op,
            Arg = arg,
            IsEmergency = emergency,
            Description = description
        };
    }

    /// <summary>
    /// CRC-16-CCITT as used by MDC1200: input bytes bit-reflected, polynomial 0x1021,
    /// initial value 0x0000, result reflected and inverted.
    /// </summary>
    internal static ushort Crc(byte[] data, int length)
    {
        ushort crc = 0x0000;
        for (int i = 0; i < length; i++)
        {
            ushort c = Reflect(data[i], 8);
            for (int mask = 0x80; mask != 0; mask >>= 1)
            {
                bool bit = (crc & 0x8000) != 0;
                crc <<= 1;
                if ((c & mask) != 0)
                    bit = !bit;
                if (bit)
                    crc ^= 0x1021;
            }
        }
        crc = Reflect(crc, 16);
        crc ^= 0xFFFF;
        return crc;
    }

    private static ushort Reflect(ushort value, int bits)
    {
        ushort result = 0;
        for (int i = 0; i < bits; i++)
        {
            if ((value & (1 << i)) != 0)
                result |= (ushort)(1 << (bits - 1 - i));
        }
        return result;
    }

    private static int PopCount(uint value) => System.Numerics.BitOperations.PopCount(value);

    private enum SlotState
    {
        Hunting,
        Collecting
    }

    private sealed class PhaseSlot
    {
        public uint Accumulator;
        public bool Inverted;
        public SlotState State;
        public uint SyncHigh;
        public uint SyncLow;
        public int BitCount;
        public readonly bool[] Bits = new bool[112];
    }
}
