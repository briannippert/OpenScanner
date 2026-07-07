using Microsoft.Extensions.Logging;
using Moq;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests;

public class Mdc1200DecoderTests
{
    private readonly Mock<ILogger<Mdc1200Decoder>> _loggerMock = new();

    private const int SampleRate = 48000;
    private const int SamplesPerBit = SampleRate / 1200; // 40

    [Fact]
    public void Decodes_Valid_Ptt_Id_Burst()
    {
        // Arrange: a PTT ID packet (op 0x01) from unit 0x1234.
        int op = 0x01, arg = 0x80, unitId = 0x1234;
        var audio = SynthesizeBurst(op, arg, unitId);

        var decoder = new Mdc1200Decoder(_loggerMock.Object, SampleRate);
        Mdc1200Packet? received = null;
        decoder.OnPacket += p => received = p;

        // Act
        decoder.ProcessAudio(audio);

        // Assert
        Assert.NotNull(received);
        Assert.Equal(unitId, received!.UnitId);
        Assert.Equal(op, received.Op);
        Assert.Equal(arg, received.Arg);
        Assert.Equal("PTT ID", received.Description);
        Assert.False(received.IsEmergency);
    }

    [Fact]
    public void Flags_Emergency_Packet()
    {
        var audio = SynthesizeBurst(0x00, 0x03, 0xABCD);
        var decoder = new Mdc1200Decoder(_loggerMock.Object, SampleRate);
        Mdc1200Packet? received = null;
        decoder.OnPacket += p => received = p;

        decoder.ProcessAudio(audio);

        Assert.NotNull(received);
        Assert.True(received!.IsEmergency);
        Assert.Equal(0xABCD, received.UnitId);
    }

    [Fact]
    public void Ignores_Noise()
    {
        var rng = new Random(1);
        var noise = new byte[SampleRate]; // 1 second
        for (int i = 0; i < noise.Length / 2; i++)
        {
            short s = (short)rng.Next(short.MinValue, short.MaxValue);
            noise[i * 2] = (byte)(s & 0xFF);
            noise[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }

        var decoder = new Mdc1200Decoder(_loggerMock.Object, SampleRate);
        bool fired = false;
        decoder.OnPacket += _ => fired = true;

        decoder.ProcessAudio(noise);

        Assert.False(fired);
    }

    /// <summary>
    /// Builds a test audio burst that the decoder's sign-at-bit-clock demodulator
    /// recovers as the intended MDC1200 bit stream: leader, 40-bit sync word, then the
    /// interleaved 112-bit frame. Each bit is emitted as one bit-period of a constant
    /// sign (+ for 1, - for 0), which is exactly what the decoder samples.
    /// </summary>
    private static byte[] SynthesizeBurst(int op, int arg, int unitId)
    {
        var onair = new List<bool>();

        // Leader: alternating bits give the decoder a clean bit clock and never match sync.
        for (int i = 0; i < 32; i++)
            onair.Add(i % 2 == 0);

        // Sync word 0x07 092a446f, MSB-first (40 bits).
        AppendBitsMsbFirst(onair, 0x07UL, 8);
        AppendBitsMsbFirst(onair, 0x092a446fUL, 32);

        // Frame: 14 bytes = op, arg, unitID hi/lo, CRC lo/hi, then unused parity bytes.
        var data = new byte[14];
        data[0] = (byte)op;
        data[1] = (byte)arg;
        data[2] = (byte)((unitId >> 8) & 0xFF);
        data[3] = (byte)(unitId & 0xFF);
        ushort crc = Mdc1200Decoder.Crc(data, 4);
        data[4] = (byte)(crc & 0xFF);
        data[5] = (byte)((crc >> 8) & 0xFF);

        // Interleave the inverse of the decoder's de-interleave: decoder reads
        // linear index L = col*7 + row from on-air position p = row*16 + col.
        var frameBits = new bool[112];
        for (int L = 0; L < 112; L++)
        {
            int b = L / 8;
            int bitPos = L % 8;
            bool value = (data[b] & (1 << bitPos)) != 0;

            int col = L / 7;
            int row = L % 7;
            int p = row * 16 + col;
            frameBits[p] = value;
        }
        onair.AddRange(frameBits);

        return BitsToAudio(onair);
    }

    private static void AppendBitsMsbFirst(List<bool> bits, ulong value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
            bits.Add(((value >> i) & 1) != 0);
    }

    private static byte[] BitsToAudio(List<bool> bits)
    {
        const short amplitude = 12000;
        var audio = new byte[bits.Count * SamplesPerBit * 2];
        int idx = 0;
        foreach (bool bit in bits)
        {
            short level = bit ? amplitude : (short)-amplitude;
            for (int s = 0; s < SamplesPerBit; s++)
            {
                audio[idx++] = (byte)(level & 0xFF);
                audio[idx++] = (byte)((level >> 8) & 0xFF);
            }
        }
        return audio;
    }
}
