using Microsoft.Extensions.Logging.Abstractions;
using OpenScanner.Server.Decoders;
using OpenScanner.Server.Models;
using Xunit;

namespace OpenScanner.Tests;

/// <summary>
/// Parses real dsd-fme stderr lines (formats from build_deps/dsd-fme/src dmr_bs.c,
/// dmr_dburst.c, dmr_ms.c and dmr_flco.c).
/// </summary>
public class DmrMetadataTests
{
    private const string Slot1Voice = "12:00:01 Sync: +DMR  [SLOT1]  slot2  | Color Code=01 | VC2 ";
    private const string Slot2Voice = "12:00:01 Sync: +DMR   slot1  [SLOT2] | Color Code=01 | VC3 ";
    private const string Slot1Lc = "\u001b[32m \n";
    private const string Slot1Call = "\u001b[32m SLOT 1 TGT=100 SRC=1234 Group Call ";

    private static (DMR Dmr, List<(int? Src, int? Tgt, string? Tone)> Hits) Make(
        int? slot = null, int? cc = null, int? tg = null)
    {
        var dmr = new DMR(NullLogger<DMR>.Instance);
        dmr.SetChannel(new Channel { Mode = "DMR", DmrSlot = slot, DmrColorCode = cc, DmrTalkgroup = tg });
        var hits = new List<(int?, int?, string?)>();
        dmr.OnActivity += (s, t, tone) => hits.Add((s, t, tone));
        return (dmr, hits);
    }

    [Fact]
    public void Voice_Burst_Is_Activity()
    {
        var (dmr, hits) = Make();
        dmr.ParseLine(Slot1Voice);
        Assert.Single(hits);
    }

    [Theory]
    [InlineData("12:00:01 Sync: +DMR  [SLOT1]  slot2  | Color Code=01 | VC1*")]
    [InlineData("12:00:01 Sync: +DMR MS/DM MODE/MONO | Color Code=01 | VC* ")]
    public void Late_Entry_And_Simplex_Voice_Are_Activity(string line)
    {
        var (dmr, hits) = Make(slot: 1);
        dmr.ParseLine(line);
        Assert.Single(hits);
    }

    [Theory]
    [InlineData("12:00:01 Sync: +DMR  [slot1]  slot2  | Color Code=01 | IDLE ")]
    [InlineData("12:00:01 Sync: +DMR  [slot1]  slot2  | Color Code=01 | CSBK ")]
    [InlineData("12:00:01 Sync:  DMR                  | VOICE CACH/EMB ERR")]
    [InlineData("12:00:01 Sync:  DMR  [SLOT1]  slot2  | Frame Sync Err: 7")]
    public void Idle_Data_And_Errors_Are_Not_Activity(string line)
    {
        var (dmr, hits) = Make();
        dmr.ParseLine(line);
        Assert.Empty(hits);
    }

    [Fact]
    public void Other_Slot_Is_Filtered()
    {
        var (dmr, hits) = Make(slot: 1);
        dmr.ParseLine(Slot2Voice);
        dmr.ParseLine("\u001b[32m SLOT 2 TGT=100 SRC=1234 Group Call ");
        Assert.Empty(hits);
        dmr.ParseLine(Slot1Voice);
        Assert.Single(hits);
    }

    [Fact]
    public void Call_Lc_Parses_Source_And_Talkgroup()
    {
        var (dmr, hits) = Make();
        dmr.ParseLine(Slot1Lc);
        dmr.ParseLine(Slot1Call);
        Assert.Equal([(1234, 100, (string?)null)], hits);
    }

    [Fact]
    public void Wrong_Color_Code_Rejects_Voice_And_Its_Call_Lc()
    {
        var (dmr, hits) = Make(cc: 2);
        dmr.ParseLine(Slot1Voice); // CC 01
        dmr.ParseLine(Slot1Call);
        Assert.Empty(hits);

        dmr.ParseLine("12:00:02 Sync: +DMR  [SLOT1]  slot2  | Color Code=02 | VC4 ");
        Assert.Single(hits);
    }

    [Fact]
    public void Wrong_Talkgroup_Rejects_Call_And_Following_Voice()
    {
        var (dmr, hits) = Make(tg: 200);
        dmr.ParseLine(Slot1Call); // TG 100
        dmr.ParseLine(Slot1Voice);
        Assert.Empty(hits);

        // Slot goes idle, next call is the held talkgroup.
        dmr.ParseLine("12:00:05 Sync: +DMR  [slot1]  slot2  | Color Code=01 | IDLE ");
        dmr.ParseLine("\u001b[32m SLOT 1 TGT=200 SRC=55 Group Call ");
        dmr.ParseLine(Slot1Voice);
        Assert.Equal(2, hits.Count);
        Assert.Equal(200, hits[0].Tgt);
    }

    [Theory]
    [InlineData("\u001b[32m SLOT 1 TGT=100 SRC=1234 Group \u001b[31mEmergency Call ")]
    [InlineData(" SLOT 1 TGT=100 SRC=1234 Group Emergency \u001b[31mEncrypted Call ")]
    public void Emergency_Call_Flags_EMRG(string line)
    {
        var (dmr, hits) = Make();
        dmr.ParseLine(line);
        Assert.Equal([(1234, 100, "EMRG")], hits);
    }

    [Fact]
    public void Voice_Burst_Is_Never_An_Emergency()
    {
        var (dmr, hits) = Make();
        dmr.ParseLine(Slot1Voice);
        Assert.Null(hits.Single().Tone);
    }
}
