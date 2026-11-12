using OpenScanner.Server.Models;

namespace OpenScanner.Server.Devices;

/// <summary>
/// Turns the channels one SDR carries into scan banks. If they all fit in one capture
/// window they share a single FastScan bank; otherwise each channel gets its own
/// FrequencyHop bank.
/// </summary>
public static class ScanBanks
{
    /// <summary>
    /// The widest channel spread one dongle can capture at once, in MHz (the 2.4 MS/s
    /// ceiling of a stable RTL-SDR).
    /// </summary>
    public const double MaxSpanMHz = 2.4;

    /// <summary>Tolerance for floating-point frequency comparisons.</summary>
    public const double Epsilon = 1e-9;

    /// <summary>
    /// Keep-out radius around the tuner centre, in MHz. One detection FFT bin at the 1.024 MHz
    /// scan rate is 1 kHz, so 10 kHz is comfortably clear of the residual DC bin.
    /// </summary>
    public const double DcGuardMHz = 0.01;

    /// <summary>Offset from a lone channel to the tuner centre, keeping it off the DC spike.</summary>
    public const double SingleChannelOffsetMHz = 0.25;

    /// <summary>How long a FrequencyHop bank listens before moving on.</summary>
    public const int HopDwellMs = 1500;

    /// <summary>Whether a set of frequencies fits in one capture window.</summary>
    public static bool Fits(double minMHz, double maxMHz) => maxMHz - minMHz <= MaxSpanMHz + Epsilon;

    /// <summary>
    /// Builds the scan banks for one SDR:
    /// <list type="bullet">
    /// <item>FastScan: the channels span ≤ 2.4 MHz, so one bank covers them all simultaneously.</item>
    /// <item>FrequencyHop: they don't, so each channel gets its own bank and the SDR dwells on each in turn.</item>
    /// </list>
    /// </summary>
    public static List<ScanBank> Build(IEnumerable<Channel> channels, ILogger? logger = null)
    {
        var sorted = channels.OrderBy(c => c.Frequency).ToList();
        if (sorted.Count == 0) return new List<ScanBank>();

        var minFreq = sorted[0].Frequency;
        var maxFreq = sorted[^1].Frequency;
        var spread = maxFreq - minFreq;

        if (Fits(minFreq, maxFreq))
        {
            // The centre must not land on a channel, or that channel sits on the DC spike and
            // is the one channel the scanner can never hear. A single channel is offset by a
            // fixed 0.25 MHz; the multi-channel midpoint was previously assumed to be clear of
            // any channel, which is false whenever the set is symmetric about one of its own
            // members (three channels evenly spaced, say).
            double center = sorted.Count == 1
                ? sorted[0].Frequency + SingleChannelOffsetMHz
                : NudgeOffChannels((minFreq + maxFreq) / 2.0, sorted, spread);

            logger?.LogInformation($"ScanBank FastScan: {minFreq:F3}-{maxFreq:F3} MHz (spread: {spread:F2} MHz)");
            return new List<ScanBank>
            {
                new()
                {
                    CenterFrequency = center,
                    Frequencies = sorted.Select(c => c.Frequency).ToList(),
                    SpreadMHz = spread,
                    Mode = ScanMode.FastScan,
                    DwellTimeMs = 0,
                },
            };
        }

        // Offset each centre by +0.25 MHz so the channel doesn't land on the DC spike bin.
        return sorted.Select(ch =>
        {
            logger?.LogInformation($"ScanBank FrequencyHop: {ch.Frequency:F3} MHz");
            return new ScanBank
            {
                CenterFrequency = ch.Frequency + SingleChannelOffsetMHz,
                Frequencies = new List<double> { ch.Frequency },
                SpreadMHz = 0,
                Mode = ScanMode.FrequencyHop,
                DwellTimeMs = HopDwellMs,
            };
        }).ToList();
    }

    /// <summary>
    /// Shifts a proposed tuner centre until no channel sits within <see cref="DcGuardMHz"/> of
    /// it, while keeping every channel inside the capture window. Returns the original centre
    /// if no clear offset exists — the window is the harder constraint, and losing one channel
    /// to the DC spike beats losing several off the edge.
    /// </summary>
    public static double NudgeOffChannels(double center, List<Channel> sorted, double spreadMHz)
    {
        // How much the centre may move before a channel falls outside the 2.4 MHz window.
        double slack = (MaxSpanMHz - spreadMHz) / 2.0;
        if (slack <= 0) return center;

        bool IsClear(double candidate) =>
            sorted.All(c => Math.Abs(c.Frequency - candidate) > DcGuardMHz);

        if (IsClear(center)) return center;

        // Try progressively larger nudges either side, staying inside the window.
        for (double step = DcGuardMHz; step <= slack; step += DcGuardMHz)
        {
            if (IsClear(center + step)) return center + step;
            if (IsClear(center - step)) return center - step;
        }
        return center;
    }
}
