using OpenScanner.Server.Models;

namespace OpenScanner.Server.Devices;

/// <summary>What one SDR does under a plan.</summary>
public enum SdrRole
{
    /// <summary>Captures one window and channelizes every channel in it at once.</summary>
    FastScan,

    /// <summary>Hops channel to channel, locking on to whichever is active.</summary>
    Hop,

    /// <summary>Has nothing to do.</summary>
    Idle,
}

/// <summary>The channels one SDR carries, and how.</summary>
public record DeviceAssignment(SdrDeviceConfig Device, SdrRole Role, IReadOnlyList<Channel> Channels, IReadOnlyList<ScanBank> Banks)
{
    /// <summary>The mix of analog and digital channels on this SDR.</summary>
    public GainClass GainClass => GainClasses.ForChannels(Channels);

    /// <summary>
    /// The tuner gain for a capture. A FastScan SDR hears all its channels through one gain, so
    /// it uses the gain for its whole mix. A hopping SDR restarts the capture for every hop, so
    /// it can use each channel's own gain; pass that channel.
    /// </summary>
    public double GainFor(Channel? channel = null) =>
        Role == SdrRole.Hop && channel != null
            ? Device.GainFor(channel.GainClass)
            : Device.GainFor(GainClass);
}

/// <summary>Which SDR carries each channel.</summary>
/// <param name="Devices">One assignment per usable SDR, in the order the SDRs were given.</param>
/// <param name="AutoPlaced">Manual mode: channels the user didn't pin (or pinned to an absent SDR) that the planner placed.</param>
/// <param name="Unscanned">Channels no SDR carries.</param>
public record SdrPlan(IReadOnlyList<DeviceAssignment> Devices, IReadOnlyList<Channel> AutoPlaced, IReadOnlyList<Channel> Unscanned)
{
    /// <summary>The number of channels that are fast-scanned, i.e. never missed while another is active.</summary>
    public int FastScannedCount => Devices.Where(d => d.Role == SdrRole.FastScan).Sum(d => d.Channels.Count);

    /// <summary>The number of channels the plan covers, fast-scanned or hopped.</summary>
    public int ScannedCount => Devices.Sum(d => d.Channels.Count);

    /// <summary>The SDR carrying a frequency, or null if none does.</summary>
    public DeviceAssignment? OwnerOf(double frequencyMHz) =>
        Devices.FirstOrDefault(d => d.Channels.Any(c => Math.Abs(c.Frequency - frequencyMHz) < 1e-6));
}

/// <summary>
/// Divides channels between SDRs. Pure and deterministic: the same inputs always give the same plan.
///
/// <para><b>Auto</b> maximises the number of fast-scanned channels. With S SDRs it first tries to
/// cover every channel with S capture windows. If that's impossible one SDR has to hop through
/// whatever is left over, so it maximises coverage with S−1 windows and gives the rest to the
/// hopper. Coverage always wins. Between layouts that cover equally many channels, it prefers
/// the one whose SDRs carry only analog or only digital channels, because each can then run at
/// the gain that suits it. Any SDR left over after full coverage splits a mixed window into an
/// analog half and a digital half.</para>
///
/// <para><b>Manual</b> honours the user's pins. Each SDR fast-scans its pinned channels if they
/// fit in a window and hops through them otherwise. Unpinned channels, and channels pinned to an
/// SDR that is absent or disabled, are placed automatically. They go first into a pinned window
/// of the same gain class they fit in, then onto SDRs with no pins (planned as Auto). A channel
/// that would only be hopped there joins a pinned window of the other class instead, if it fits.
/// Anything left goes onto a hopping SDR. A channel only goes unscanned if placing it would mean
/// overriding a pin.</para>
/// </summary>
public static class SdrPlanner
{
    /// <summary>Plans which SDR carries each channel.</summary>
    /// <param name="channels">The channels to scan (already excluding avoided ones).</param>
    /// <param name="devices">The usable SDRs (attached and enabled), in preference order.</param>
    /// <param name="mode">Auto or Manual assignment.</param>
    /// <param name="previous">
    /// The plan currently running, if any. Auto mode keeps each window on the SDR that already
    /// carries most of it, so a small channel edit doesn't reshuffle and restart every dongle.
    /// </param>
    public static SdrPlan Plan(
        IReadOnlyList<Channel> channels,
        IReadOnlyList<SdrDeviceConfig> devices,
        SdrAssignmentMode mode,
        SdrPlan? previous = null)
    {
        var sorted = channels.OrderBy(c => c.Frequency).ToList();
        if (devices.Count == 0)
            return new SdrPlan(Array.Empty<DeviceAssignment>(), Array.Empty<Channel>(), sorted);

        return mode == SdrAssignmentMode.Manual
            ? PlanManual(sorted, devices, previous)
            : PlanAuto(sorted, devices, previous);
    }

    // ---------------------------------------------------------------- Auto

    private static SdrPlan PlanAuto(List<Channel> sorted, IReadOnlyList<SdrDeviceConfig> devices, SdrPlan? previous)
    {
        var groups = AutoGroups(sorted, devices.Count);
        var assignments = MapGroupsToDevices(groups, devices, previous);
        return new SdrPlan(assignments, Array.Empty<Channel>(), Array.Empty<Channel>());
    }

    /// <summary>
    /// Divides channels into at most <paramref name="sdrCount"/> groups, one per SDR: capture
    /// windows plus, if not everything fits, one group of leftovers to hop through.
    /// </summary>
    private static List<List<Channel>> AutoGroups(List<Channel> sorted, int sdrCount)
    {
        var groups = new List<List<Channel>>();
        if (sorted.Count == 0 || sdrCount == 0) return groups;

        var full = BestWindows(sorted, sdrCount);
        if (full.Covered == sorted.Count)
        {
            groups.AddRange(full.Windows);
            SplitMixedWindows(groups, spare: sdrCount - groups.Count);
            return groups;
        }

        // Not everything fits in windows. One SDR on its own can't do both, so it hops
        // through everything, exactly as before multi-SDR support.
        if (sdrCount == 1)
        {
            groups.Add(sorted);
            return groups;
        }

        // Keep one SDR back to hop through whatever the windows don't cover.
        var partial = BestWindows(sorted, sdrCount - 1);
        groups.AddRange(partial.Windows);
        var covered = partial.Windows.SelectMany(w => w).ToHashSet();
        groups.Add(sorted.Where(c => !covered.Contains(c)).ToList());
        return groups;
    }

    private record Layout(List<List<Channel>> Windows, int Covered)
    {
        public int MixedCount => Windows.Count(w => GainClasses.ForChannels(w) == GainClass.Mixed);
    }

    /// <summary>
    /// The best layout of up to <paramref name="windowCount"/> capture windows: most channels
    /// covered, then fewest mixed windows, then fewest windows.
    /// </summary>
    private static Layout BestWindows(List<Channel> sorted, int windowCount)
    {
        // Ignoring gain class: the most channels any layout can cover.
        var best = MaxCoverage(sorted, windowCount);

        // Keeping classes apart: try every split of the windows between analog and digital.
        var analog = sorted.Where(c => c.GainClass == GainClass.Analog).ToList();
        var digital = sorted.Where(c => c.GainClass == GainClass.Digital).ToList();
        for (int forAnalog = 0; forAnalog <= windowCount; forAnalog++)
        {
            var a = MaxCoverage(analog, forAnalog);
            var d = MaxCoverage(digital, windowCount - forAnalog);
            var pure = new Layout(a.Windows.Concat(d.Windows).ToList(), a.Covered + d.Covered);
            if (IsBetter(pure, best)) best = pure;
        }
        return best;
    }

    private static bool IsBetter(Layout x, Layout y)
    {
        if (x.Covered != y.Covered) return x.Covered > y.Covered;
        if (x.MixedCount != y.MixedCount) return x.MixedCount < y.MixedCount;
        return x.Windows.Count < y.Windows.Count;
    }

    /// <summary>
    /// The most channels <paramref name="windowCount"/> capture windows can cover, and the
    /// windows that do it. Dynamic programming over channels sorted by frequency, O(n·k).
    ///
    /// <para>An optimal layout can always be made of disjoint runs of consecutive channels, and a
    /// window ending at channel i is never worse for starting as early as the span allows: moving
    /// its start one channel earlier covers one more channel and costs the earlier windows at most
    /// one. So <c>best[k][i]</c>, the most of the first i channels that k windows cover, is the
    /// better of leaving channel i−1 uncovered and ending a window there.</para>
    /// </summary>
    private static Layout MaxCoverage(List<Channel> sorted, int windowCount)
    {
        int n = sorted.Count;
        if (n == 0 || windowCount <= 0) return new Layout(new List<List<Channel>>(), 0);

        // start[i]: the earliest channel a window ending at channel i can include.
        var start = new int[n];
        for (int i = 0, s = 0; i < n; i++)
        {
            while (!ScanBanks.Fits(sorted[s].Frequency, sorted[i].Frequency)) s++;
            start[i] = s;
        }

        var best = new int[windowCount + 1, n + 1];
        var endsWindow = new bool[windowCount + 1, n + 1];
        for (int k = 1; k <= windowCount; k++)
        {
            for (int i = 1; i <= n; i++)
            {
                int skip = best[k, i - 1];
                int s = start[i - 1];
                int take = best[k - 1, s] + (i - s);
                // Prefer ending a window on ties: it uses the window budget no less well and
                // reconstructs to fewer, fuller windows.
                if (take >= skip)
                {
                    best[k, i] = take;
                    endsWindow[k, i] = true;
                }
                else
                {
                    best[k, i] = skip;
                }
            }
        }

        var windows = new List<List<Channel>>();
        for (int k = windowCount, i = n; k > 0 && i > 0;)
        {
            if (endsWindow[k, i])
            {
                int s = start[i - 1];
                windows.Add(sorted.GetRange(s, i - s));
                i = s;
                k--;
            }
            else
            {
                i--;
            }
        }
        windows.Reverse();
        return new Layout(windows, best[windowCount, n]);
    }

    /// <summary>
    /// Uses spare SDRs to split mixed windows into an analog and a digital window, largest
    /// first. Each half is a subset of a window that already fits, so it fits too.
    /// </summary>
    private static void SplitMixedWindows(List<List<Channel>> groups, int spare)
    {
        while (spare > 0)
        {
            var mixed = groups
                .Where(g => GainClasses.ForChannels(g) == GainClass.Mixed)
                .OrderByDescending(g => g.Count)
                .FirstOrDefault();
            if (mixed == null) return;

            int at = groups.IndexOf(mixed);
            groups[at] = mixed.Where(c => c.GainClass == GainClass.Analog).ToList();
            groups.Insert(at + 1, mixed.Where(c => c.GainClass == GainClass.Digital).ToList());
            spare--;
        }
    }

    /// <summary>
    /// Gives each group an SDR. A group goes back to the SDR that carried most of its channels
    /// in the previous plan; the rest go to free SDRs in order. SDRs without a group are idle.
    /// </summary>
    private static List<DeviceAssignment> MapGroupsToDevices(
        List<List<Channel>> groups, IReadOnlyList<SdrDeviceConfig> devices, SdrPlan? previous)
    {
        var owner = new string?[groups.Count];
        var taken = new HashSet<string>();

        if (previous != null)
        {
            var previousFreqs = previous.Devices.ToDictionary(
                d => d.Device.Id,
                d => d.Channels.Select(c => c.Frequency).ToHashSet());

            var pairs =
                from g in Enumerable.Range(0, groups.Count)
                from d in devices
                let overlap = previousFreqs.TryGetValue(d.Id, out var f) ? groups[g].Count(c => f.Contains(c.Frequency)) : 0
                where overlap > 0
                orderby overlap descending, g, IndexOf(devices, d)
                select (Group: g, Device: d.Id);

            foreach (var (g, d) in pairs)
            {
                if (owner[g] != null || taken.Contains(d)) continue;
                owner[g] = d;
                taken.Add(d);
            }
        }

        var free = new Queue<string>(devices.Select(d => d.Id).Where(id => !taken.Contains(id)));
        for (int g = 0; g < groups.Count; g++)
            owner[g] ??= free.Dequeue();

        return devices.Select(d =>
        {
            int g = Array.IndexOf(owner, d.Id);
            return g < 0 ? Idle(d) : Assign(d, groups[g]);
        }).ToList();
    }

    private static int IndexOf(IReadOnlyList<SdrDeviceConfig> devices, SdrDeviceConfig device)
    {
        for (int i = 0; i < devices.Count; i++)
            if (ReferenceEquals(devices[i], device)) return i;
        return -1;
    }

    // ---------------------------------------------------------------- Manual

    private static SdrPlan PlanManual(List<Channel> sorted, IReadOnlyList<SdrDeviceConfig> devices, SdrPlan? previous)
    {
        var usable = devices.Select(d => d.Id).ToHashSet();
        var groups = devices.ToDictionary(d => d.Id, _ => new List<Channel>());
        var orphans = new List<Channel>();

        foreach (var ch in sorted)
        {
            if (ch.SdrDeviceId != null && usable.Contains(ch.SdrDeviceId))
                groups[ch.SdrDeviceId].Add(ch);
            else
                orphans.Add(ch);
        }

        var pinnedIds = groups.Where(g => g.Value.Count > 0).Select(g => g.Key).ToHashSet();
        var placed = new List<Channel>();
        var unscanned = new List<Channel>();

        bool TryJoinWindow(Channel ch, bool sameClassOnly)
        {
            var target = devices.FirstOrDefault(d =>
                pinnedIds.Contains(d.Id)
                && FitsWith(groups[d.Id], ch)
                && (!sameClassOnly || GainClasses.ForChannels(groups[d.Id]) == ch.GainClass));
            if (target == null) return false;
            groups[target.Id].Add(ch);
            placed.Add(ch);
            return true;
        }

        // 1. Into a pinned SDR's capture window of the same gain class, if the channel fits
        //    without pushing any pinned channel out. It's fast-scanned and costs no SDR.
        var remaining = orphans.Where(ch => !TryJoinWindow(ch, sameClassOnly: true)).ToList();

        // 2. Onto the SDRs with no pins, planned exactly like Auto mode. Any channel that plan
        //    would only hop is fast-scanned instead if it fits a pinned window of the other
        //    class: coverage beats gain purity. Moving channels out can free the hopper to
        //    become another window, so re-plan until nothing moves.
        var free = devices.Where(d => !pinnedIds.Contains(d.Id)).ToList();
        while (remaining.Count > 0)
        {
            var hopped = free.Count == 0
                ? remaining
                : AutoGroups(remaining, free.Count).Where(g => !FitsInWindow(g)).SelectMany(g => g).ToList();
            var moved = hopped.Where(ch => TryJoinWindow(ch, sameClassOnly: false)).ToHashSet();
            if (moved.Count == 0) break;
            remaining.RemoveAll(moved.Contains);
        }

        if (remaining.Count > 0 && free.Count > 0)
        {
            foreach (var a in MapGroupsToDevices(AutoGroups(remaining, free.Count), free, previous))
                groups[a.Device.Id].AddRange(a.Channels);
            placed.AddRange(remaining);
            remaining.Clear();
        }

        // 3. Onto an SDR that hops anyway: one more hop costs nothing but dwell time.
        //    Spread them over the hoppers, fewest channels first.
        foreach (var ch in remaining)
        {
            var hopper = devices
                .Where(d => pinnedIds.Contains(d.Id) && !FitsInWindow(groups[d.Id]))
                .OrderBy(d => groups[d.Id].Count)
                .FirstOrDefault();
            if (hopper != null)
            {
                groups[hopper.Id].Add(ch);
                placed.Add(ch);
            }
            else
            {
                // Every SDR is fast-scanning pinned channels this one doesn't fit beside.
                // Placing it would mean turning a pinned SDR into a hopper: leave that to the user.
                unscanned.Add(ch);
            }
        }

        var assignments = devices
            .Select(d => groups[d.Id].Count == 0 ? Idle(d) : Assign(d, groups[d.Id].OrderBy(c => c.Frequency).ToList()))
            .ToList();
        return new SdrPlan(assignments, placed.OrderBy(c => c.Frequency).ToList(), unscanned);
    }

    private static bool FitsInWindow(List<Channel> group) =>
        group.Count == 0 || ScanBanks.Fits(group.Min(c => c.Frequency), group.Max(c => c.Frequency));

    private static bool FitsWith(List<Channel> group, Channel ch) =>
        FitsInWindow(group) && ScanBanks.Fits(
            Math.Min(ch.Frequency, group.Min(c => c.Frequency)),
            Math.Max(ch.Frequency, group.Max(c => c.Frequency)));

    // ---------------------------------------------------------------- helpers

    private static DeviceAssignment Assign(SdrDeviceConfig device, List<Channel> channels)
    {
        var banks = ScanBanks.Build(channels);
        var role = banks.Count == 1 && banks[0].Mode == ScanMode.FastScan ? SdrRole.FastScan : SdrRole.Hop;
        return new DeviceAssignment(device, role, channels, banks);
    }

    private static DeviceAssignment Idle(SdrDeviceConfig device) =>
        new(device, SdrRole.Idle, Array.Empty<Channel>(), Array.Empty<ScanBank>());
}
