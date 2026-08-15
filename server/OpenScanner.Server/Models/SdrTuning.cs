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
public readonly record struct SdrTuning(double GainDb = 0, double Ppm = 0)
{
    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

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
    public string RtlFmArgs() => $"-g {Num(GainDb)} -p {Num(Ppm)} -F 9 -E dc";
}
