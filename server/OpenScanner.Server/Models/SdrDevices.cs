namespace OpenScanner.Server.Models;

/// <summary>
/// Which tuner gain a channel wants. Digital voice (P25 C4FM, DMR 4FSK) is decoded symbol by
/// symbol and suffers badly from front-end overload and intermodulation, so it usually wants a
/// lower, fixed gain. Analog FM/AM is listened to directly and benefits from more gain on weak
/// signals.
/// </summary>
public enum GainClass
{
    /// <summary>Analog voice (NFM, FM, AM, WFM): demodulated in-process.</summary>
    Analog,

    /// <summary>Digital voice (P25, DMR): decoded by dsd-fme.</summary>
    Digital,

    /// <summary>An SDR carrying both analog and digital channels at once.</summary>
    Mixed,
}

/// <summary>Classifies channel modes into <see cref="GainClass"/>es.</summary>
public static class GainClasses
{
    /// <summary>
    /// The gain class of a channel mode. This is also the single definition of which modes
    /// need a dsd-fme decoder process: anything that isn't digital is demodulated in-process.
    /// </summary>
    public static GainClass ForMode(string? mode) =>
        mode?.ToUpperInvariant() switch
        {
            "P25" or "DMR" => GainClass.Digital,
            _ => GainClass.Analog,
        };

    /// <summary>The gain class of a group of channels sharing one SDR.</summary>
    public static GainClass ForChannels(IEnumerable<Channel> channels)
    {
        bool analog = false, digital = false;
        foreach (var ch in channels)
        {
            if (ch.GainClass == GainClass.Digital) digital = true; else analog = true;
            if (analog && digital) return GainClass.Mixed;
        }
        return digital ? GainClass.Digital : GainClass.Analog;
    }
}

/// <summary>How channels are divided between SDRs.</summary>
public enum SdrAssignmentMode
{
    /// <summary>The planner divides channels to maximise fast-scan coverage.</summary>
    Auto,

    /// <summary>The user pins channels to SDRs; unpinned channels are placed automatically.</summary>
    Manual,
}

/// <summary>
/// An RTL-SDR dongle as currently attached, from librtlsdr's device listing.
/// </summary>
/// <param name="Index">The librtlsdr index. Not stable: it can change when dongles are replugged.</param>
/// <param name="Vendor">USB manufacturer string, e.g. "Realtek".</param>
/// <param name="Product">USB product string, e.g. "RTL2838UHIDIR".</param>
/// <param name="Serial">USB serial string; may be empty, and is often shared ("00000001").</param>
/// <param name="Id">
/// The stable identity used to remember this dongle: <c>sn:&lt;serial&gt;</c> when the serial is
/// unique among the attached dongles, otherwise <c>idx:&lt;index&gt;</c>.
/// </param>
/// <param name="SerialIsUnique">False when another attached dongle reports the same serial.</param>
public record SdrDeviceInfo(int Index, string Vendor, string Product, string Serial, string Id, bool SerialIsUnique);

/// <summary>
/// The stored settings for one SDR dongle (the <c>sdr_devices</c> table).
/// </summary>
public class SdrDeviceConfig
{
    /// <summary>Stable identity; see <see cref="SdrDeviceInfo.Id"/>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>User-facing name, e.g. "Roof antenna".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The USB serial last seen for this dongle.</summary>
    public string Serial { get; set; } = string.Empty;

    /// <summary>The USB product string last seen for this dongle.</summary>
    public string Product { get; set; } = string.Empty;

    /// <summary>Whether OpenScanner may use this dongle.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Crystal frequency error correction, in ppm. Every dongle's crystal differs.</summary>
    public double Ppm { get; set; }

    /// <summary>Tuner gain (dB, 0 = AGC) when this SDR carries only analog channels.</summary>
    public double GainAnalog { get; set; }

    /// <summary>Tuner gain (dB, 0 = AGC) when this SDR carries only digital channels.</summary>
    public double GainDigital { get; set; }

    /// <summary>Tuner gain (dB, 0 = AGC) when this SDR carries both.</summary>
    public double GainMixed { get; set; }

    /// <summary>Display and tie-break order.</summary>
    public int SortOrder { get; set; }

    /// <summary>A copy, for editing without disturbing a plan that holds this one.</summary>
    public SdrDeviceConfig Clone() => (SdrDeviceConfig)MemberwiseClone();

    /// <summary>The gain to run at for a given mix of channels.</summary>
    public double GainFor(GainClass gainClass) => gainClass switch
    {
        GainClass.Digital => GainDigital,
        GainClass.Mixed => GainMixed,
        _ => GainAnalog,
    };
}

/// <summary>Live state of one SDR, for the UI.</summary>
/// <param name="Id">Stable identity (<see cref="SdrDeviceConfig.Id"/>).</param>
/// <param name="Label">User-facing name.</param>
/// <param name="Product">USB product string.</param>
/// <param name="Serial">USB serial string.</param>
/// <param name="Attached">Whether the dongle is plugged in.</param>
/// <param name="Enabled">Whether OpenScanner may use it.</param>
/// <param name="Role">"FastScan", "Hop" or "Idle".</param>
/// <param name="Status">The SDR's own scanner status ("SCANNING", "RECEIVING", …).</param>
/// <param name="IsHardwareConnected">Whether its capture is currently delivering samples.</param>
/// <param name="GainClass">"Analog", "Digital" or "Mixed".</param>
/// <param name="Gain">Tuner gain in use, dB (0 = AGC).</param>
/// <param name="CenterFrequency">Tuner centre for a FastScan SDR, MHz.</param>
/// <param name="CurrentFrequency">The channel a hopping SDR is on or locked to, MHz.</param>
/// <param name="Frequencies">The channels this SDR carries, MHz.</param>
public record SdrDeviceState(
    string Id,
    string Label,
    string Product,
    string Serial,
    bool Attached,
    bool Enabled,
    string Role,
    string Status,
    bool IsHardwareConnected,
    string GainClass,
    double? Gain,
    double? CenterFrequency,
    double? CurrentFrequency,
    double[] Frequencies);
