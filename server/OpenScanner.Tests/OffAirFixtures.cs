using OpenScanner.Server;
using OpenScanner.Server.DSP;
using OpenScanner.Server.Devices;
using Xunit;

namespace OpenScanner.Tests;

/// <summary>
/// The off-air P25 and DMR captures in TestData/OffAir (see its README) and helpers for
/// feeding them to the decoders.
/// </summary>
internal static class OffAirFixtures
{
    public const double ChannelMhz = 158.400;
    // Fixtures are 96 kS/s with the channel 25 kHz below centre.
    public const double CenterMhz = 158.425;
    public const int SampleRate = 96000;

    public const string P25 = "p25_nac113_tg1";
    public const string Dmr = "dmr_simplex_ts1_cc9_tg763901";

    public const int SourceId = 12345678;
    public const int P25Talkgroup = 1;
    public const int DmrTalkgroup = 763901;

    public static string PathOf(string file) => Path.Combine(AppContext.BaseDirectory, "TestData", "OffAir", file);

    public static byte[] ReadIq(string fixture) => File.ReadAllBytes(PathOf(fixture + ".iq.u8"));

    /// <summary>
    /// dsd-fme's stderr from decoding a fixture. <paramref name="build"/> picks a log from a
    /// specific dsd-fme build (e.g. "d6b64f3"); line order differs between builds.
    /// </summary>
    public static IEnumerable<string> ReadLog(string fixture, string? build = null) =>
        File.ReadLines(PathOf(fixture + (build == null ? ".dsdfme.log" : $".dsdfme-{build}.log")));

    /// <summary>
    /// Channelizes a fixture with the production filter profile and writes the 48 kHz s16le
    /// FM discriminator output to a temp file: the stream a decoder reads from rtl_fm or stdin.
    /// The caller deletes the file. <paramref name="skipSeconds"/> drops the start of the stream,
    /// to join a call already in progress the way the scanner does after it detects a signal.
    /// </summary>
    public static string WriteDiscriminatorFile(string fixture, string mode, double skipSeconds = 0)
    {
        var (cutoff, scaleRef) = ChannelPipeline.FilterProfile(mode);
        var ch = new Channelizer(SampleRate, 48000, (ChannelMhz - CenterMhz) * 1e6, false, cutoff, scaleRef);
        var iq = ReadIq(fixture);
        var path = Path.Combine(Path.GetTempPath(), $"openscanner-{fixture}-{Guid.NewGuid():N}.s16");
        using var f = File.Create(path);
        var outBuf = new byte[ch.MaxOutputBytes(16384)];
        long toSkip = (long)(skipSeconds * 48000) * 2;
        for (int off = 0; off < iq.Length; off += 16384)
        {
            int len = Math.Min(16384, iq.Length - off);
            int n = ch.ProcessIQ(iq[off..(off + len)], len, outBuf);
            int drop = (int)Math.Min(toSkip, n);
            toSkip -= drop;
            f.Write(outBuf, drop, n - drop);
        }
        return path;
    }

    /// <summary>
    /// Whether dsd-fme and ffmpeg are installed. CI builds both, so there a missing one fails the
    /// test; on a dev machine without them the caller returns early.
    /// </summary>
    public static bool DecoderToolsAvailable()
    {
        if (Path.IsPathRooted(PlatformTools.DsdFme) && Path.IsPathRooted(PlatformTools.Ffmpeg)) return true;
        Assert.False(Environment.GetEnvironmentVariable("CI") == "true",
            "dsd-fme and ffmpeg must be installed in CI for the off-air decode tests");
        return false;
    }

    /// <summary>True if a 16-bit PCM chunk has a sample above dsd-fme's inter-call filler.</summary>
    public static bool IsLoud(byte[] chunk)
    {
        for (int i = 0; i + 1 < chunk.Length; i += 8)
        {
            short s = (short)(chunk[i] | (chunk[i + 1] << 8));
            if (s > 1200 || s < -1200) return true;
        }
        return false;
    }
}
