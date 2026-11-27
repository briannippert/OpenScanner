using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using OpenScanner.Server.Decoders;
using OpenScanner.Server.Devices;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using Xunit;

namespace OpenScanner.Tests.Decoders;

/// <summary>
/// Real off-air P25 and DMR calls (TestData/OffAir, see its README) run through the actual
/// FastScan path: <see cref="ChannelPipeline"/> -> Channelizer -> dsd-fme -> decoder parser.
///
/// The end-to-end tests need dsd-fme and ffmpeg. CI builds both, so there a missing dsd-fme is
/// a failure; on a dev machine without it those tests return early. The log-replay tests at
/// the bottom need nothing and always run.
/// </summary>
public class OffAirDecodeTests
{
    private const double ChannelMhz = OffAirFixtures.ChannelMhz;
    private const string P25Fixture = OffAirFixtures.P25;
    private const string DmrFixture = OffAirFixtures.Dmr;
    private const int SourceId = OffAirFixtures.SourceId;

    // ---- End-to-end through ChannelPipeline + dsd-fme ----

    [Fact]
    public async Task P25_DecodesTalkgroupSourceNacAndVoice()
    {
        if (!OffAirFixtures.DecoderToolsAvailable()) return;

        var r = await RunPipeline(P25Fixture, new Channel { Frequency = ChannelMhz, Mode = "P25", AlphaTag = "P25" });

        Assert.Contains(r.Metadata, l => l.Contains("NAC/CC: 113"));
        Assert.Contains(r.Metadata, l => l.Contains("TDU"));
        Assert.True(r.Activity.Count(a => a.Src == SourceId && a.Tgt == 1) >= 5,
            $"expected repeated Group 1 / Source {SourceId} activity, got: {Describe(r.Activity)}");
        Assert.DoesNotContain(r.Activity, a => a.Tone == "EMRG");
        r.AssertVoice(3.0); // 10 LDU1/LDU2 pairs = 3.6 s of voice
    }

    [Fact]
    public async Task DmrSimplex_DecodesTalkgroupSourceColorCodeAndVoice()
    {
        if (!OffAirFixtures.DecoderToolsAvailable()) return;

        var r = await RunPipeline(DmrFixture, DmrChannel(cc: 9, tg: 763901));

        Assert.Contains(r.Metadata, l => l.Contains("MS/DM") && l.Contains("Color Code=09"));
        Assert.Contains(r.Metadata, l => l.Contains("TLC"));
        Assert.True(r.Activity.Count(a => a.Src == SourceId && a.Tgt == 763901) >= 5,
            $"expected repeated TG 763901 / SRC {SourceId} activity, got: {Describe(r.Activity)}");
        // Bit errors occasionally pass dsd-fme's checks with a wrong talkgroup; the filter
        // must keep those out.
        Assert.All(r.Activity, a => Assert.True(a.Tgt is null or 763901, $"leaked TG {a.Tgt}"));
        r.AssertVoice(3.5); // 12 voice superframes = 4.3 s of voice
    }

    [Theory]
    [InlineData(3, null)]      // co-channel user on another color code
    [InlineData(null, 1)]      // another talkgroup
    public async Task DmrSimplex_OtherColorCodeOrTalkgroup_RaisesNoActivity(int? cc, int? tg)
    {
        if (!OffAirFixtures.DecoderToolsAvailable()) return;

        var r = await RunPipeline(DmrFixture, DmrChannel(cc, tg));

        // The call was decoded — just filtered out.
        Assert.Contains(r.Metadata, l => l.Contains("TGT=763901"));
        Assert.Empty(r.Activity);
    }

    // ---- Captured dsd-fme logs through the parsers (no dsd-fme needed) ----

    [Fact]
    public void P25Log_ParsesGroupAndSource()
    {
        var p25 = new P25(NullLogger<P25>.Instance);
        var hits = new List<(int? Src, int? Tgt, string? Tone)>();
        p25.OnActivity += (s, t, tone) => hits.Add((s, t, tone));

        foreach (var line in OffAirFixtures.ReadLog(P25Fixture)) p25.ParseLine(line);

        Assert.True(hits.Count(h => h.Src == SourceId && h.Tgt == 1) >= 5, Describe(hits));
        Assert.DoesNotContain(hits, h => h.Tone == "EMRG");
    }

    [Theory]
    [InlineData(1, 9, 763901, true)]
    [InlineData(null, null, null, true)]
    [InlineData(2, null, null, false)]      // simplex is always slot 1
    [InlineData(1, 3, null, false)]
    [InlineData(1, null, 1, false)]
    public void DmrLog_FiltersBySlotColorCodeAndTalkgroup(int? slot, int? cc, int? tg, bool expectCall)
    {
        var dmr = new DMR(NullLogger<DMR>.Instance);
        dmr.SetChannel(new Channel { Mode = "DMR", DmrSlot = slot, DmrColorCode = cc, DmrTalkgroup = tg });
        var hits = new List<(int? Src, int? Tgt, string? Tone)>();
        dmr.OnActivity += (s, t, tone) => hits.Add((s, t, tone));

        foreach (var line in OffAirFixtures.ReadLog(DmrFixture)) dmr.ParseLine(line);

        if (expectCall)
            Assert.True(hits.Count(h => h.Src == SourceId && h.Tgt == 763901) >= 5, Describe(hits));
        else
            Assert.Empty(hits);
    }

    // ---- Harness ----

    private sealed record PipelineResult(
        List<(int? Src, int? Tgt, string? Tone)> Activity, List<string> Metadata, double AudioSeconds, double LoudSeconds)
    {
        /// <summary>dsd-fme only emits audio for decoded voice frames; pauses between words are quiet.</summary>
        public void AssertVoice(double minAudioSeconds)
        {
            Assert.True(AudioSeconds >= minAudioSeconds, $"only {AudioSeconds:F1}s of decoded audio");
            Assert.True(LoudSeconds >= 1.0, $"audio is near-silent ({LoudSeconds:F1}s above the silence floor)");
        }
    }

    private static Channel DmrChannel(int? cc, int? tg) => new()
    {
        Frequency = ChannelMhz, Mode = "DMR", AlphaTag = "DMR", DmrSlot = 1, DmrColorCode = cc, DmrTalkgroup = tg,
    };

    private static async Task<PipelineResult> RunPipeline(string fixture, Channel channel)
    {
        var iq = OffAirFixtures.ReadIq(fixture);

        var activity = new List<(int?, int?, string?)>();
        var metadata = new List<string>();
        long audioBytes = 0, loudBytes = 0;
        var gate = new object();
        var lastEvent = Stopwatch.StartNew();
        var started = new TaskCompletionSource();

        using var pipeline = new ChannelPipeline(
            channel, OffAirFixtures.SampleRate, 48000, OffAirFixtures.CenterMhz, new Factory(), NullLogger.Instance, NullLoggerFactory.Instance);

        pipeline.OnMetadata += (_, line) =>
        {
            lock (gate) { metadata.Add(Ansi.Replace(line, "")); lastEvent.Restart(); }
            started.TrySetResult();
        };
        pipeline.OnActivity += (_, s, t, tone) => { lock (gate) { activity.Add((s, t, tone)); lastEvent.Restart(); } };
        pipeline.OnAudio += (_, chunk) =>
        {
            bool loud = OffAirFixtures.IsLoud(chunk);
            lock (gate) { audioBytes += chunk.Length; if (loud) loudBytes += chunk.Length; lastEvent.Restart(); }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        pipeline.Start(cts.Token);

        // The decoder process starts asynchronously and input fed before it is up is dropped;
        // dsd-fme's start-up banner on stderr says it is reading.
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));

        const int chunkBytes = 16384;
        for (int off = 0; off < iq.Length; off += chunkBytes)
        {
            int len = Math.Min(chunkBytes, iq.Length - off);
            pipeline.ProcessIQ(iq[off..(off + len)], len);
        }

        // dsd-fme keeps waiting for input, so wait until its output goes quiet.
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(200);
            lock (gate) if (lastEvent.Elapsed > TimeSpan.FromSeconds(2)) break;
        }

        lock (gate)
            return new PipelineResult([.. activity], [.. metadata], audioBytes / 96000.0, loudBytes / 96000.0);
    }

    private sealed class Factory : IDecoderFactory
    {
        public IDecoder GetDecoder(string? mode) => mode switch
        {
            "P25" => new P25(NullLogger<P25>.Instance),
            "DMR" => new DMR(NullLogger<DMR>.Instance),
            _ => throw new ArgumentException(mode),
        };
    }

    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);





    private static string Describe(IEnumerable<(int? Src, int? Tgt, string? Tone)> hits) =>
        string.Join(", ", hits.GroupBy(h => h).Select(g => $"{g.Key.Src}/{g.Key.Tgt}/{g.Key.Tone}x{g.Count()}"));
}
