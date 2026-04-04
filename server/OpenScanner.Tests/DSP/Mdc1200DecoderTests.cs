using OpenScanner.Server.DSP;
using Xunit;

namespace OpenScanner.Tests.DSP;

public class Mdc1200DecoderTests
{
    private const int SampleRate = 48000;
    private const int BaudRate = 1200;
    private const int SamplesPerBit = SampleRate / BaudRate; // 40
    private const double MarkFreq = 1200.0;
    private const double SpaceFreq = 1800.0;

    /// <summary>
    /// Generate s16le PCM for one bit of FSK: mark (1200 Hz) for 1, space (1800 Hz) for 0.
    /// </summary>
    private static void GenerateBit(byte[] buffer, ref int offset, int bit, ref double phase)
    {
        double freq = bit == 1 ? MarkFreq : SpaceFreq;
        for (int i = 0; i < SamplesPerBit; i++)
        {
            double sample = Math.Sin(phase) * 0.8; // 80% amplitude
            short s16 = (short)(sample * 32767);
            buffer[offset++] = (byte)(s16 & 0xFF);
            buffer[offset++] = (byte)((s16 >> 8) & 0xFF);
            phase += 2.0 * Math.PI * freq / SampleRate;
            if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
        }
    }

    /// <summary>
    /// Generate a complete MDC1200 burst: preamble + 40-bit codeword.
    /// </summary>
    private static byte[] GenerateMdc1200Burst(byte op, byte arg, int unitId, int preambleBits = 40)
    {
        byte idHi = (byte)((unitId >> 8) & 0xFF);
        byte idLo = (byte)(unitId & 0xFF);
        byte crc = (byte)(~(op + arg + idHi + idLo) & 0xFF);

        // Total bits: preamble + 40 data bits
        int totalBits = preambleBits + 40;
        int totalSamples = totalBits * SamplesPerBit;
        byte[] pcm = new byte[totalSamples * 2]; // s16le
        int offset = 0;
        double phase = 0;

        // Preamble: alternating 1/0
        for (int i = 0; i < preambleBits; i++)
        {
            GenerateBit(pcm, ref offset, i % 2 == 0 ? 1 : 0, ref phase);
        }

        // Data: op(8) + arg(8) + idHi(8) + idLo(8) + crc(8) MSB first
        byte[] dataBytes = { op, arg, idHi, idLo, crc };
        foreach (byte b in dataBytes)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                GenerateBit(pcm, ref offset, (b >> bit) & 1, ref phase);
            }
        }

        return pcm;
    }

    [Fact]
    public void DecodesPttIdBurst()
    {
        var decoder = new Mdc1200Decoder();
        int? decodedUnitId = null;
        byte? decodedOp = null;

        decoder.OnPacket += (uid, op, arg) =>
        {
            decodedUnitId = uid;
            decodedOp = op;
        };

        byte[] burst = GenerateMdc1200Burst(Mdc1200Decoder.OpPttIdPre, 0x80, 0x1234);
        decoder.Process(burst, burst.Length);

        Assert.NotNull(decodedUnitId);
        Assert.Equal(0x1234, decodedUnitId!.Value);
        Assert.Equal(Mdc1200Decoder.OpPttIdPre, decodedOp!.Value);
    }

    [Fact]
    public void DecodesEmergencyBurst()
    {
        var decoder = new Mdc1200Decoder();
        int? decodedUnitId = null;
        byte? decodedOp = null;

        decoder.OnPacket += (uid, op, arg) =>
        {
            decodedUnitId = uid;
            decodedOp = op;
        };

        byte[] burst = GenerateMdc1200Burst(Mdc1200Decoder.OpEmergency, 0x00, 0xABCD);
        decoder.Process(burst, burst.Length);

        Assert.NotNull(decodedUnitId);
        Assert.Equal(0xABCD, decodedUnitId!.Value);
        Assert.Equal(Mdc1200Decoder.OpEmergency, decodedOp!.Value);
    }

    [Fact]
    public void DecodesPostPttId()
    {
        var decoder = new Mdc1200Decoder();
        int? decodedUnitId = null;

        decoder.OnPacket += (uid, op, arg) => decodedUnitId = uid;

        byte[] burst = GenerateMdc1200Burst(Mdc1200Decoder.OpPttIdPost, 0x80, 0x0042);
        decoder.Process(burst, burst.Length);

        Assert.NotNull(decodedUnitId);
        Assert.Equal(0x0042, decodedUnitId!.Value);
    }

    [Fact]
    public void RejectsCorruptedCrc()
    {
        var decoder = new Mdc1200Decoder();
        bool decoded = false;

        decoder.OnPacket += (uid, op, arg) => decoded = true;

        // Generate valid burst then corrupt one byte
        byte[] burst = GenerateMdc1200Burst(Mdc1200Decoder.OpPttIdPre, 0x80, 0x1234);

        // Corrupt the CRC byte (last 8 bits = last 8*40 = 320 samples = 640 bytes of PCM)
        // Flip the last data bit by overwriting some of the last bit's samples with opposite freq
        int lastBitStart = burst.Length - SamplesPerBit * 2; // second-to-last bit
        double phase = 0;
        // Write opposite frequency to corrupt data
        for (int i = 0; i < SamplesPerBit; i++)
        {
            double sample = Math.Sin(phase) * 0.8;
            short s16 = (short)(sample * 32767);
            int idx = lastBitStart + i * 2;
            if (idx + 1 < burst.Length)
            {
                burst[idx] = (byte)(s16 & 0xFF);
                burst[idx + 1] = (byte)((s16 >> 8) & 0xFF);
            }
            // Use mark freq where space was expected or vice versa
            phase += 2.0 * Math.PI * MarkFreq / SampleRate;
        }

        decoder.Process(burst, burst.Length);
        Assert.False(decoded);
    }

    [Fact]
    public void DoesNotFireOnSilence()
    {
        var decoder = new Mdc1200Decoder();
        bool decoded = false;

        decoder.OnPacket += (uid, op, arg) => decoded = true;

        // Feed silence (all zeros)
        byte[] silence = new byte[SampleRate * 2]; // 1 second
        decoder.Process(silence, silence.Length);

        Assert.False(decoded);
    }

    [Fact]
    public void DoesNotFireOnRandomNoise()
    {
        var decoder = new Mdc1200Decoder();
        bool decoded = false;

        decoder.OnPacket += (uid, op, arg) => decoded = true;

        // Generate random noise
        var rng = new Random(42);
        byte[] noise = new byte[SampleRate * 2 * 5]; // 5 seconds of noise
        rng.NextBytes(noise);

        decoder.Process(noise, noise.Length);
        Assert.False(decoded);
    }

    [Fact]
    public void HandlesChunkedInput()
    {
        var decoder = new Mdc1200Decoder();
        int? decodedUnitId = null;

        decoder.OnPacket += (uid, op, arg) => decodedUnitId = uid;

        byte[] burst = GenerateMdc1200Burst(Mdc1200Decoder.OpPttIdPre, 0x80, 0x5678);

        // Feed in small chunks (simulating real-time audio processing)
        int chunkSize = 960; // 10ms at 48kHz s16le
        for (int i = 0; i < burst.Length; i += chunkSize)
        {
            int len = Math.Min(chunkSize, burst.Length - i);
            byte[] chunk = new byte[len];
            Buffer.BlockCopy(burst, i, chunk, 0, len);
            decoder.Process(chunk, len);
        }

        Assert.NotNull(decodedUnitId);
        Assert.Equal(0x5678, decodedUnitId!.Value);
    }

    [Fact]
    public void CooldownPreventsDuplicates()
    {
        var decoder = new Mdc1200Decoder();
        int decodeCount = 0;

        decoder.OnPacket += (uid, op, arg) => decodeCount++;

        // Same burst twice back to back
        byte[] burst = GenerateMdc1200Burst(Mdc1200Decoder.OpPttIdPre, 0x80, 0x1234);
        decoder.Process(burst, burst.Length);
        decoder.Process(burst, burst.Length);

        // Should only fire once due to cooldown
        Assert.Equal(1, decodeCount);
    }

    [Fact]
    public void DecodesAfterReset()
    {
        var decoder = new Mdc1200Decoder();
        int decodeCount = 0;

        decoder.OnPacket += (uid, op, arg) => decodeCount++;

        byte[] burst = GenerateMdc1200Burst(Mdc1200Decoder.OpPttIdPre, 0x80, 0x1234);
        decoder.Process(burst, burst.Length);
        Assert.Equal(1, decodeCount);

        // Reset clears cooldown, should decode again
        decoder.Reset();
        decoder.Process(burst, burst.Length);
        Assert.Equal(2, decodeCount);
    }

    [Fact]
    public void DecodesWithLongPreamble()
    {
        var decoder = new Mdc1200Decoder();
        int? decodedUnitId = null;

        decoder.OnPacket += (uid, op, arg) => decodedUnitId = uid;

        // Leading MDC burst has ~520ms preamble = ~624 bits
        byte[] burst = GenerateMdc1200Burst(Mdc1200Decoder.OpPttIdPre, 0x80, 0xDEEE, preambleBits: 200);
        decoder.Process(burst, burst.Length);

        Assert.NotNull(decodedUnitId);
        Assert.Equal(0xDEEE, decodedUnitId!.Value);
    }

    [Fact]
    public void DecodesCallAlert()
    {
        var decoder = new Mdc1200Decoder();
        byte? decodedOp = null;
        byte? decodedArg = null;

        decoder.OnPacket += (uid, op, arg) =>
        {
            decodedOp = op;
            decodedArg = arg;
        };

        byte[] burst = GenerateMdc1200Burst(Mdc1200Decoder.OpCallAlert, 0x42, 0x0100);
        decoder.Process(burst, burst.Length);

        Assert.Equal(Mdc1200Decoder.OpCallAlert, decodedOp);
        Assert.Equal((byte)0x42, decodedArg);
    }

    [Fact]
    public void DecodesEmbeddedInVoiceAudio()
    {
        var decoder = new Mdc1200Decoder();
        int? decodedUnitId = null;

        decoder.OnPacket += (uid, op, arg) => decodedUnitId = uid;

        // Create a buffer with: 500ms silence + MDC burst + 500ms silence
        byte[] burst = GenerateMdc1200Burst(Mdc1200Decoder.OpPttIdPre, 0x80, 0x4321);
        int silenceBytes = SampleRate * 2 / 2; // 500ms
        byte[] combined = new byte[silenceBytes + burst.Length + silenceBytes];
        Buffer.BlockCopy(burst, 0, combined, silenceBytes, burst.Length);

        decoder.Process(combined, combined.Length);

        Assert.NotNull(decodedUnitId);
        Assert.Equal(0x4321, decodedUnitId!.Value);
    }

    [Theory]
    [InlineData(0x0001)]
    [InlineData(0x0999)]
    [InlineData(0x1234)]
    [InlineData(0xDEEE)]
    [InlineData(0x0000)]
    public void DecodesVariousUnitIds(int unitId)
    {
        var decoder = new Mdc1200Decoder();
        int? decodedUnitId = null;

        decoder.OnPacket += (uid, op, arg) => decodedUnitId = uid;

        byte[] burst = GenerateMdc1200Burst(Mdc1200Decoder.OpPttIdPre, 0x80, unitId);
        decoder.Process(burst, burst.Length);

        Assert.NotNull(decodedUnitId);
        Assert.Equal(unitId, decodedUnitId!.Value);
    }

    [Fact]
    public void DescribeOpcodeReturnsCorrectStrings()
    {
        Assert.Equal("PTT-ID (pre)", Mdc1200Decoder.DescribeOpcode(Mdc1200Decoder.OpPttIdPre));
        Assert.Equal("PTT-ID (post)", Mdc1200Decoder.DescribeOpcode(Mdc1200Decoder.OpPttIdPost));
        Assert.Equal("EMERGENCY", Mdc1200Decoder.DescribeOpcode(Mdc1200Decoder.OpEmergency));
        Assert.Equal("Emergency Ack", Mdc1200Decoder.DescribeOpcode(Mdc1200Decoder.OpEmergencyAck));
        Assert.Equal("Call Alert", Mdc1200Decoder.DescribeOpcode(Mdc1200Decoder.OpCallAlert));
        Assert.Equal("Op 0xFF", Mdc1200Decoder.DescribeOpcode(0xFF));
    }
}
