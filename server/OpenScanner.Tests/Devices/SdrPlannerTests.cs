using OpenScanner.Server.Devices;
using OpenScanner.Server.Models;
using Xunit;

namespace OpenScanner.Tests.Devices;

public class SdrPlannerTests
{
    private static Channel Ch(double freq, string mode = "NFM", string? pin = null) =>
        new() { Frequency = freq, Mode = mode, AlphaTag = $"{freq}", SdrDeviceId = pin };

    private static SdrDeviceConfig Sdr(string id, double analog = 40, double digital = 30, double mixed = 35) =>
        new() { Id = id, Label = id, GainAnalog = analog, GainDigital = digital, GainMixed = mixed };

    private static SdrPlan Auto(IEnumerable<Channel> channels, params SdrDeviceConfig[] sdrs) =>
        SdrPlanner.Plan(channels.ToList(), sdrs, SdrAssignmentMode.Auto);

    private static SdrPlan Manual(IEnumerable<Channel> channels, params SdrDeviceConfig[] sdrs) =>
        SdrPlanner.Plan(channels.ToList(), sdrs, SdrAssignmentMode.Manual);

    private static double[] Freqs(DeviceAssignment a) => a.Channels.Select(c => c.Frequency).ToArray();

    private static DeviceAssignment On(SdrPlan plan, string id) => plan.Devices.Single(d => d.Device.Id == id);

    // ------------------------------------------------------------ single SDR parity

    [Fact]
    public void OneSdr_ChannelsFit_FastScansThemExactlyAsBefore()
    {
        var channels = new[] { Ch(155.0), Ch(155.5), Ch(156.0) };

        var a = Assert.Single(Auto(channels, Sdr("A")).Devices);

        Assert.Equal(SdrRole.FastScan, a.Role);
        var expected = Assert.Single(ScanBanks.Build(channels));
        var bank = Assert.Single(a.Banks);
        Assert.Equal(expected.CenterFrequency, bank.CenterFrequency);
        Assert.Equal(expected.Frequencies, bank.Frequencies);
    }

    [Fact]
    public void OneSdr_ChannelsTooWide_HopsThroughAllOfThem()
    {
        var a = Assert.Single(Auto(new[] { Ch(155.0), Ch(156.0), Ch(460.0) }, Sdr("A")).Devices);

        Assert.Equal(SdrRole.Hop, a.Role);
        Assert.Equal(3, a.Banks.Count);
        Assert.All(a.Banks, b => Assert.Equal(ScanMode.FrequencyHop, b.Mode));
    }

    // ------------------------------------------------------------ auto coverage

    [Fact]
    public void TwoSdrs_TwoClusters_FastScanBoth()
    {
        var plan = Auto(new[] { Ch(155.0), Ch(155.4), Ch(460.0), Ch(460.3) }, Sdr("A"), Sdr("B"));

        Assert.All(plan.Devices, d => Assert.Equal(SdrRole.FastScan, d.Role));
        Assert.Equal(new[] { 155.0, 155.4 }, Freqs(On(plan, "A")));
        Assert.Equal(new[] { 460.0, 460.3 }, Freqs(On(plan, "B")));
        Assert.Equal(4, plan.FastScannedCount);
    }

    /// <summary>
    /// Three clusters, two SDRs: one SDR must hop whatever the other can't cover, so the window
    /// goes to the biggest cluster.
    /// </summary>
    [Fact]
    public void MoreClustersThanSdrs_WindowTakesTheBiggestCluster_HopperTakesTheRest()
    {
        var big = new[] { Ch(460.0), Ch(460.1), Ch(460.2), Ch(460.3), Ch(460.4) };
        var channels = big.Concat(new[] { Ch(155.0), Ch(155.2), Ch(851.0) }).ToList();

        var plan = Auto(channels, Sdr("A"), Sdr("B"));

        var fast = Assert.Single(plan.Devices, d => d.Role == SdrRole.FastScan);
        Assert.Equal(big.Select(c => c.Frequency), Freqs(fast));
        var hop = Assert.Single(plan.Devices, d => d.Role == SdrRole.Hop);
        Assert.Equal(new[] { 155.0, 155.2, 851.0 }, Freqs(hop));
        Assert.Equal(8, plan.ScannedCount);
        Assert.Empty(plan.Unscanned);
    }

    /// <summary>
    /// With full coverage achievable, every SDR is a window and nobody hops, even if the
    /// clusters are uneven.
    /// </summary>
    [Fact]
    public void ThreeSdrs_ThreeClusters_NobodyHops()
    {
        var plan = Auto(new[] { Ch(155.0), Ch(460.0), Ch(460.1), Ch(851.0) }, Sdr("A"), Sdr("B"), Sdr("C"));
        Assert.All(plan.Devices, d => Assert.Equal(SdrRole.FastScan, d.Role));
        Assert.Equal(4, plan.FastScannedCount);
    }

    [Fact]
    public void SpareSdrs_AreIdle()
    {
        var plan = Auto(new[] { Ch(155.0), Ch(155.1) }, Sdr("A"), Sdr("B"), Sdr("C"));
        Assert.Equal(new[] { SdrRole.FastScan, SdrRole.Idle, SdrRole.Idle }, plan.Devices.Select(d => d.Role));
    }

    // ------------------------------------------------------------ gain classes

    /// <summary>
    /// Everything fits one window, but a second SDR lets analog and digital each run at their
    /// own gain, at no cost in coverage.
    /// </summary>
    [Fact]
    public void MixedCluster_WithASpareSdr_IsSplitByGainClass()
    {
        var plan = Auto(new[] { Ch(155.0), Ch(155.2, "P25"), Ch(157.0), Ch(156.0, "DMR") }, Sdr("A"), Sdr("B"));

        Assert.All(plan.Devices, d => Assert.Equal(SdrRole.FastScan, d.Role));
        Assert.Equal(
            new[] { GainClass.Analog, GainClass.Digital },
            plan.Devices.Select(d => d.GainClass).OrderBy(g => g));
        Assert.Equal(40, plan.Devices.Single(d => d.GainClass == GainClass.Analog).GainFor());
        Assert.Equal(30, plan.Devices.Single(d => d.GainClass == GainClass.Digital).GainFor());
    }

    /// <summary>
    /// Two mixed clusters on two SDRs. Separating classes would leave half the channels
    /// hopping, so both SDRs run mixed at the mixed gain: coverage comes first.
    /// </summary>
    [Fact]
    public void CoverageBeatsPurity()
    {
        var plan = Auto(new[] { Ch(155.0), Ch(155.5, "P25"), Ch(460.0), Ch(460.5, "P25") }, Sdr("A"), Sdr("B"));

        Assert.Equal(4, plan.FastScannedCount);
        Assert.All(plan.Devices, d => Assert.Equal(GainClass.Mixed, d.GainClass));
        Assert.All(plan.Devices, d => Assert.Equal(35, d.GainFor()));
    }

    /// <summary>
    /// When coverage ties, the layout with fewer mixed SDRs wins: here the leftover digital
    /// channel hops either way, so the window holds the analog cluster rather than a mix.
    /// </summary>
    [Fact]
    public void EqualCoverage_PrefersPureWindows()
    {
        // Window candidates of size 3: {155.0, 155.1, 155.2} (analog) or {155.1, 155.2, 157.5}.
        var channels = new[] { Ch(155.0), Ch(155.1), Ch(155.2), Ch(157.5, "P25"), Ch(851.0, "P25") };

        var plan = Auto(channels, Sdr("A"), Sdr("B"));

        var fast = Assert.Single(plan.Devices, d => d.Role == SdrRole.FastScan);
        Assert.Equal(GainClass.Analog, fast.GainClass);
        Assert.Equal(3, plan.FastScannedCount);
    }

    /// <summary>A hopper restarts its capture each hop, so each channel gets its own class's gain.</summary>
    [Fact]
    public void Hopper_UsesEachChannelsOwnGain()
    {
        var p25 = Ch(851.0, "P25");
        var nfm = Ch(155.0);
        var a = Assert.Single(Auto(new[] { p25, nfm }, Sdr("A")).Devices);

        Assert.Equal(SdrRole.Hop, a.Role);
        Assert.Equal(30, a.GainFor(p25));
        Assert.Equal(40, a.GainFor(nfm));
    }

    // ------------------------------------------------------------ stability

    /// <summary>
    /// A window stays on the SDR that already carries it, so re-planning after an edit
    /// doesn't restart every dongle.
    /// </summary>
    [Fact]
    public void Replanning_KeepsWindowsOnTheirSdr()
    {
        var a = Sdr("A");
        var b = Sdr("B");
        var channels = new List<Channel> { Ch(155.0), Ch(460.0) };

        // B happens to carry the low cluster.
        var previous = SdrPlanner.Plan(channels, new[] { b, a }, SdrAssignmentMode.Auto);
        Assert.Equal(new[] { 155.0 }, Freqs(On(previous, "B")));

        channels.Add(Ch(460.2));
        var next = SdrPlanner.Plan(channels, new[] { a, b }, SdrAssignmentMode.Auto, previous);

        Assert.Equal(new[] { 155.0 }, Freqs(On(next, "B")));
        Assert.Equal(new[] { 460.0, 460.2 }, Freqs(On(next, "A")));
    }

    [Fact]
    public void Plan_IsDeterministic()
    {
        var channels = new[] { Ch(155.0), Ch(460.0, "P25"), Ch(460.1), Ch(851.0, "DMR"), Ch(852.0) };
        var x = Auto(channels, Sdr("A"), Sdr("B"));
        var y = Auto(channels, Sdr("A"), Sdr("B"));
        Assert.Equal(x.Devices.Select(Freqs), y.Devices.Select(Freqs));
    }

    // ------------------------------------------------------------ edge cases

    [Fact]
    public void NoSdrs_LeavesEverythingUnscanned()
    {
        var plan = Auto(new[] { Ch(155.0) });
        Assert.Empty(plan.Devices);
        Assert.Single(plan.Unscanned);
    }

    [Fact]
    public void NoChannels_LeavesEverySdrIdle()
    {
        var plan = Auto(Array.Empty<Channel>(), Sdr("A"), Sdr("B"));
        Assert.All(plan.Devices, d => Assert.Equal(SdrRole.Idle, d.Role));
    }

    // ------------------------------------------------------------ manual

    [Fact]
    public void Manual_PinnedChannels_FastScanWhenTheyFitAndHopWhenTheyDont()
    {
        var plan = Manual(
            new[] { Ch(155.0, pin: "A"), Ch(155.5, pin: "A"), Ch(155.2, pin: "B"), Ch(460.0, pin: "B") },
            Sdr("A"), Sdr("B"));

        Assert.Equal(SdrRole.FastScan, On(plan, "A").Role);
        Assert.Equal(SdrRole.Hop, On(plan, "B").Role);
        Assert.Equal(new[] { 155.2, 460.0 }, Freqs(On(plan, "B")));
        Assert.Empty(plan.AutoPlaced);
    }

    /// <summary>An unpinned channel that fits a pinned window of its own class joins it.</summary>
    [Fact]
    public void Manual_Orphan_JoinsAMatchingPinnedWindow()
    {
        var orphan = Ch(155.3, "P25");
        var plan = Manual(
            new[] { Ch(155.0, pin: "A"), Ch(155.1, "P25", pin: "B"), orphan, Ch(460.0, pin: "C") },
            Sdr("A"), Sdr("B"), Sdr("C"));

        Assert.Contains(orphan, On(plan, "B").Channels);
        Assert.Equal(GainClass.Digital, On(plan, "B").GainClass);
        Assert.Equal(new[] { orphan }, plan.AutoPlaced);
    }

    /// <summary>Orphans that fit no pinned window go to SDRs with no pins, planned as Auto.</summary>
    [Fact]
    public void Manual_Orphans_UseFreeSdrs()
    {
        var plan = Manual(
            new[] { Ch(155.0, pin: "A"), Ch(460.0), Ch(460.2), Ch(851.0) },
            Sdr("A"), Sdr("B"), Sdr("C"));

        Assert.Equal(new[] { 155.0 }, Freqs(On(plan, "A")));
        Assert.Equal(3, plan.AutoPlaced.Count);
        Assert.Equal(4, plan.FastScannedCount);
    }

    /// <summary>
    /// The only free SDR would have to hop, but the orphan fits beside a pinned channel of the
    /// other class. Fast-scanning it beats keeping the pinned SDR pure.
    /// </summary>
    [Fact]
    public void Manual_Orphan_PrefersAnyPinnedWindowOverHopping()
    {
        var orphan = Ch(155.3);
        var plan = Manual(
            new[] { Ch(155.0, "P25", pin: "A"), orphan, Ch(460.0), Ch(851.0) },
            Sdr("A"), Sdr("B"));

        Assert.Contains(orphan, On(plan, "A").Channels);
        Assert.Equal(GainClass.Mixed, On(plan, "A").GainClass);
        Assert.Equal(SdrRole.Hop, On(plan, "B").Role);
        Assert.Equal(new[] { 460.0, 851.0 }, Freqs(On(plan, "B")));
    }

    /// <summary>A channel pinned to an unplugged or disabled SDR is placed automatically.</summary>
    [Fact]
    public void Manual_PinToAMissingSdr_IsAutoPlaced()
    {
        var stray = Ch(460.0, pin: "GONE");
        var plan = Manual(new[] { Ch(155.0, pin: "A"), stray }, Sdr("A"), Sdr("B"));

        Assert.Equal(new[] { 460.0 }, Freqs(On(plan, "B")));
        Assert.Equal(new[] { stray }, plan.AutoPlaced);
    }

    [Fact]
    public void Manual_NoFreeSdr_OrphanJoinsAHopper()
    {
        var plan = Manual(
            new[] { Ch(155.0, pin: "A"), Ch(155.1, pin: "B"), Ch(460.0, pin: "B"), Ch(851.0) },
            Sdr("A"), Sdr("B"));

        Assert.Contains(851.0, Freqs(On(plan, "B")));
        Assert.Empty(plan.Unscanned);
    }

    /// <summary>
    /// Every SDR fast-scans pinned channels the orphan doesn't fit beside. Placing it would
    /// mean overriding a pin, so it is reported as unscanned instead.
    /// </summary>
    [Fact]
    public void Manual_NoRoomWithoutOverridingAPin_LeavesItUnscanned()
    {
        var orphan = Ch(851.0);
        var plan = Manual(new[] { Ch(155.0, pin: "A"), Ch(460.0, pin: "B"), orphan }, Sdr("A"), Sdr("B"));

        Assert.Equal(new[] { orphan }, plan.Unscanned);
        Assert.All(plan.Devices, d => Assert.Equal(SdrRole.FastScan, d.Role));
    }

    [Fact]
    public void Manual_PinsAreIgnoredInAutoMode()
    {
        var plan = Auto(new[] { Ch(155.0, pin: "B"), Ch(155.1, pin: "A") }, Sdr("A"), Sdr("B"));
        Assert.Equal(new[] { 155.0, 155.1 }, Freqs(On(plan, "A")));
    }

    // ------------------------------------------------------------ optimality

    /// <summary>
    /// The fast-scanned count matches a brute-force search over every placement of windows,
    /// for random single-class channel sets (so gain purity can't affect the choice).
    /// </summary>
    [Fact]
    public void FastScannedCount_IsOptimal()
    {
        var rng = new Random(1234);
        for (int trial = 0; trial < 300; trial++)
        {
            int n = rng.Next(1, 10);
            var channels = Enumerable.Range(0, n)
                .Select(_ => Math.Round(150 + rng.NextDouble() * 12, 3))
                .Distinct()
                .Select(f => Ch(f))
                .ToList();
            int sdrs = rng.Next(1, 4);

            var plan = Auto(channels, Enumerable.Range(0, sdrs).Select(i => Sdr($"S{i}")).ToArray());

            int expected;
            if (BruteForceCoverage(channels, sdrs) == channels.Count)
                expected = channels.Count;
            else if (sdrs == 1)
                expected = 0; // one SDR that can't cover everything hops through everything
            else
                expected = BruteForceCoverage(channels, sdrs - 1);

            Assert.Equal(expected, plan.FastScannedCount);
            Assert.Equal(channels.Count, plan.ScannedCount);
        }
    }

    private static int BruteForceCoverage(List<Channel> channels, int windows)
    {
        var starts = channels.Select(c => c.Frequency).ToArray();
        int best = 0;

        void Search(int from, int left, HashSet<double> covered)
        {
            best = Math.Max(best, covered.Count);
            if (left == 0) return;
            for (int i = from; i < starts.Length; i++)
            {
                var added = channels
                    .Select(c => c.Frequency)
                    .Where(f => f >= starts[i] && f - starts[i] <= ScanBanks.MaxSpanMHz + 1e-9 && !covered.Contains(f))
                    .ToList();
                foreach (var f in added) covered.Add(f);
                Search(i + 1, left - 1, covered);
                foreach (var f in added) covered.Remove(f);
            }
        }

        Search(0, windows, new HashSet<double>());
        return best;
    }
}
