using System.Globalization;

namespace OpenScanner.Server.Models;

/// <summary>
/// The RTL-SDR front-end settings shared by every capture, and the rtl_fm flags that carry
/// them. Kept in one place because the decoders used to hardcode their own values — gain was
/// 42 in three of them and 45 in the fourth, ppm was pinned at 0 everywhere, and the wideband
/// scan ran at a completely different gain from the decoders it handed off to.
/// </summary>
/// <param name="GainDb">Tuner gain in dB; 0 selects the tuner's own AGC.</param>
/// <param name="Ppm">Crystal frequency error correction, in parts per million.</param>
/// <param name="DeviceIndex">
/// The librtlsdr device index to open, or null for the tool's default (index 0). This is
/// deliberately an index and not a serial: <c>-d</c> tries an all-digit argument as an index
/// before it tries it as a serial, so the stock serials (<c>00000001</c>, <c>00000002</c>, …)
/// would silently open the wrong dongle. Callers resolve a device's serial to its current
/// index with <see cref="Devices.RtlDeviceEnumerator"/> just before launching a capture.
/// </param>
public readonly record struct SdrTuning(double GainDb = 0, double Ppm = 0, int? DeviceIndex = null)
{
    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// The flags rtl_sdr and rtl_fm share: device, tuner gain and crystal correction. Both
    /// tools spell them the same way and both treat <c>-g 0</c> as AUTO.
    /// </summary>
    public string CaptureArgs() =>
        (DeviceIndex is int d ? $"-d {d} " : "") + $"-g {Num(GainDb)} -p {Num(Ppm)}";

    /// <summary>
    /// The rtl_fm front-end flags. Beyond gain and ppm these carry two corrections that were
    /// missing entirely:
    ///
    /// <c>-F 9</c> enables rtl_fm's low-leakage downsample filter. Its own help text describes
    /// the default as having "bad roll off", and for narrowband work that roll-off is the
    /// difference between hearing the wanted channel and hearing it plus its neighbours.
    ///
    /// <c>-E dc</c> removes the DC offset that would otherwise sit in the middle of the
    /// demodulated audio as a constant bias.
    /// </summary>
    /// <param name="includeFilters">
    /// False drops <c>-F 9 -E dc</c>, for wideband FM where the narrowband filter isn't wanted.
    /// </param>
    public string RtlFmArgs(bool includeFilters = true) =>
        includeFilters ? $"{CaptureArgs()} -F 9 -E dc" : CaptureArgs();
}
