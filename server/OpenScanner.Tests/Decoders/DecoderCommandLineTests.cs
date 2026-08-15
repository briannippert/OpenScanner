using Microsoft.Extensions.Logging.Abstractions;
using OpenScanner.Server.Decoders;
using OpenScanner.Server.Models;
using Xunit;

namespace OpenScanner.Tests.Decoders;

/// <summary>
/// Pins the rtl_fm front-end flags. These are one-character options buried in long shell
/// pipelines, easy to lose in a refactor and invisible when lost — the radio simply receives
/// worse. Each assertion here corresponds to a specific reception defect.
/// </summary>
public class DecoderCommandLineTests
{
    private static readonly Channel Vhf = new() { Frequency = 155.460, Mode = "P25", AlphaTag = "TEST" };

    private static string CommandFor(DSDBase decoder, SdrTuning? tuning = null)
    {
        decoder.Tuning = tuning ?? new SdrTuning(GainDb: 38.5, Ppm: -27);
        return decoder.GetCommandLine(Vhf);
    }

    private static DSDBase[] AllDecoders() =>
    [
        new P25(NullLogger<P25>.Instance),
        new DMR(NullLogger<DMR>.Instance),
        new NFM(NullLogger<NFM>.Instance),
        new AM(NullLogger<AM>.Instance),
    ];

    /// <summary>
    /// rtl_fm's own help calls the default downsample roll-off "bad". For 12.5 kHz channels
    /// that roll-off decides whether the neighbouring transmitter is audible underneath.
    /// </summary>
    [Fact]
    public void EveryDecoder_EnablesTheLowLeakageDownsampleFilter()
    {
        foreach (var decoder in AllDecoders())
            Assert.Contains("-F 9", CommandFor(decoder));
    }

    /// <summary>
    /// Frequency correction used to be hardcoded to "-p 0". A generic dongle is commonly
    /// 20-60 ppm out, which at VHF puts the signal outside a narrowband passband entirely.
    /// </summary>
    [Fact]
    public void EveryDecoder_PassesTheConfiguredPpm()
    {
        foreach (var decoder in AllDecoders())
        {
            var cmd = CommandFor(decoder);
            Assert.Contains("-p -27", cmd);
            Assert.DoesNotContain("-p 0 ", cmd);
        }
    }

    /// <summary>
    /// Gain was hardcoded at 42 in three decoders and 45 in the fourth, while the wideband
    /// scan that decides when to hand off ran at 20 — so detection was ~22 dB deafer than
    /// reception, and a signal could be perfectly listenable yet never found.
    /// </summary>
    [Fact]
    public void EveryDecoder_PassesTheConfiguredGain()
    {
        foreach (var decoder in AllDecoders())
        {
            var cmd = CommandFor(decoder);
            Assert.Contains("-g 38.5", cmd);
            Assert.DoesNotContain("-g 42", cmd);
            Assert.DoesNotContain("-g 45", cmd);
        }
    }

    [Fact]
    public void EveryDecoder_EnablesDcBlocking()
    {
        foreach (var decoder in AllDecoders())
            Assert.Contains("-E dc", CommandFor(decoder));
    }

    /// <summary>
    /// The regression this exists to prevent: squelch in front of dsd-fme. rtl_fm's squelch
    /// gates the sample stream, and a digital decoder needs a continuous one to hold symbol
    /// sync. P25 shipped "-l 50" for years.
    /// </summary>
    [Theory]
    [InlineData("P25")]
    [InlineData("DMR")]
    public void DigitalDecoders_NeverSquelchAheadOfDsdFme(string mode)
    {
        DSDBase decoder = mode == "P25"
            ? new P25(NullLogger<P25>.Instance)
            : new DMR(NullLogger<DMR>.Instance);

        var cmd = CommandFor(decoder);
        Assert.Contains("-l 0", cmd);
        Assert.DoesNotContain("-l 50", cmd);
    }

    /// <summary>
    /// Analog voice keeps its squelch — nothing downstream needs an unbroken stream, and it
    /// saves the CPU of demodulating dead air.
    /// </summary>
    [Theory]
    [InlineData("NFM")]
    [InlineData("AM")]
    public void AnalogDecoders_KeepTheirSquelch(string mode)
    {
        DSDBase decoder = mode == "NFM"
            ? new NFM(NullLogger<NFM>.Instance)
            : new AM(NullLogger<AM>.Instance);

        Assert.Contains("-l 50", CommandFor(decoder));
    }

    /// <summary>
    /// In parallel FastScan the caller supplies channelized samples over stdin, so the decoder
    /// must not launch a second rtl_fm and fight for the USB device.
    /// </summary>
    [Fact]
    public void WithAnInputSource_NoRtlFmIsLaunched()
    {
        var decoder = new P25(NullLogger<P25>.Instance) { InputSource = "cat -" };
        var cmd = CommandFor(decoder);

        Assert.DoesNotContain("rtl_fm", cmd);
        Assert.Contains("cat -", cmd);
    }

    /// <summary>Default tuning is AUTO gain and no correction, matching rtl_fm's own defaults.</summary>
    [Fact]
    public void DefaultTuning_SelectsAutoGainAndNoCorrection()
    {
        Assert.Equal("-g 0 -p 0 -F 9 -E dc", new SdrTuning().RtlFmArgs());
    }

    /// <summary>
    /// Arguments are built with the invariant culture: on a locale that formats decimals with
    /// a comma, "38,5" would be parsed by rtl_fm as 38 and the fractional part silently lost.
    /// </summary>
    [Fact]
    public void FractionalValues_UseInvariantFormatting()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("-g 38.5 -p -1.25 -F 9 -E dc", new SdrTuning(38.5, -1.25).RtlFmArgs());
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
