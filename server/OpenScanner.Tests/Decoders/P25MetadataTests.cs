using Microsoft.Extensions.Logging.Abstractions;
using OpenScanner.Server.Decoders;
using Xunit;

namespace OpenScanner.Tests;

/// <summary>
/// Parses real dsd-fme stderr lines (formats from build_deps/dsd-fme/src/p25_lcw.c).
/// </summary>
public class P25MetadataTests
{
    private static (int? Src, int? Tgt, string? Tone)? Parse(string line)
    {
        var p25 = new P25(NullLogger<P25>.Instance);
        (int?, int?, string?)? result = null;
        p25.OnActivity += (src, tgt, tone) => result = (src, tgt, tone);
        p25.ParseLine(line);
        return result;
    }

    [Fact]
    public void Group_Call_Without_Emergency_Has_No_Tone()
    {
        var r = Parse("\u001b[33m Group Voice Channel User - Group 100 Source 1234\u001b[0m");
        Assert.Equal((1234, 100, (string?)null), r);
    }

    [Theory]
    [InlineData(" Emergency Group Voice Channel User - Group 100 Source 1234")]
    [InlineData("\u001b[33m Emergency Group Voice Channel User - Group 100 Source 1234\u001b[0m")]
    [InlineData("\u001b[33m Emergency Encrypted Group Voice Channel User - Group 100 Source 1234\u001b[0m")]
    public void Group_Call_With_Emergency_Flags_EMRG(string line)
    {
        Assert.Equal((1234, 100, "EMRG"), Parse(line));
    }

    [Fact]
    public void Unit_To_Unit_Emergency_Flags_EMRG()
    {
        var r = Parse("\u001b[33m Emergency Unit to Unit Voice Channel User - Target 555 Source 1234\u001b[0m");
        Assert.Equal((1234, 555, "EMRG"), r);
    }

    [Theory]
    [InlineData(" [EMERGENCY]")]
    [InlineData(" EMERGENCY ")]
    public void Emergency_Only_Line_Still_Raises_Activity(string line)
    {
        Assert.Equal((null, null, "EMRG"), Parse(line));
    }

    [Fact]
    public void Tsbk_Emergency_Is_Ignored()
    {
        // A control-channel grant may still count as activity, but never as an emergency.
        Assert.Null(Parse(" P25 TSBK Emergency Group Voice Channel Grant")?.Tone);
    }

    [Fact]
    public void Emergency_Substring_Does_Not_Match()
    {
        // Whole-word match only.
        Assert.Null(Parse(" Emergencyish"));
    }
}
