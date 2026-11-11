using OpenScanner.Server.Devices;
using OpenScanner.Server.Models;
using Xunit;

namespace OpenScanner.Tests.Devices;

/// <summary>
/// A FastScan bank tunes the SDR to one centre frequency and channelizes outwards from it.
/// Whatever sits at that centre lands on the dongle's DC spike, so the centre must never be a
/// channel — otherwise there is exactly one channel in the bank the scanner can never hear,
/// silently, with no error anywhere.
/// </summary>
public class ScanCenterTests
{
    private static List<Channel> At(params double[] frequencies) =>
        frequencies.Select(f => new Channel { Frequency = f, AlphaTag = $"CH{f}" }).ToList();

    private static double Spread(List<Channel> channels) =>
        channels.Max(c => c.Frequency) - channels.Min(c => c.Frequency);

    private static double Center(List<Channel> channels)
    {
        var sorted = channels.OrderBy(c => c.Frequency).ToList();
        double midpoint = (sorted.First().Frequency + sorted.Last().Frequency) / 2.0;
        return RtlDevice.NudgeOffChannels(midpoint, sorted, Spread(sorted));
    }

    /// <summary>
    /// The case that motivated this: three evenly spaced channels put the middle one exactly on
    /// the midpoint. The old code took the midpoint unconditionally, on the assumption that a
    /// multi-channel centre was "already offset from DC".
    /// </summary>
    [Fact]
    public void EvenlySpacedChannels_DoNotLeaveOneOnTheCentre()
    {
        var channels = At(154.100, 154.250, 154.400);
        double center = Center(channels);

        Assert.All(channels, c =>
            Assert.True(Math.Abs(c.Frequency - center) > 0.005,
                $"{c.AlphaTag} at {c.Frequency} sits {Math.Abs(c.Frequency - center) * 1000:F1} kHz from centre {center}"));
    }

    /// <summary>Two channels straddle their midpoint already, so nothing should move.</summary>
    [Fact]
    public void ClearMidpoint_IsLeftAlone()
    {
        var channels = At(154.100, 154.400);
        Assert.Equal(154.250, Center(channels), 6);
    }

    /// <summary>
    /// Nudging must not push a channel outside the 2.4 MHz capture window. When the set already
    /// fills the window there is no room, and keeping every channel in view beats rescuing one
    /// from the DC spike.
    /// </summary>
    [Fact]
    public void FullWidthBank_IsNotNudgedOutOfTheWindow()
    {
        var channels = At(150.000, 151.200, 152.400);
        double center = Center(channels);

        Assert.Equal(151.200, center, 6);
        Assert.All(channels, c => Assert.True(Math.Abs(c.Frequency - center) <= 1.2 + 1e-9));
    }

    /// <summary>
    /// Every channel must stay inside the window after any nudge, across a range of layouts.
    /// </summary>
    [Theory]
    [InlineData(new[] { 154.100, 154.250, 154.400 })]
    [InlineData(new[] { 460.0, 460.5, 461.0, 461.5 })]
    [InlineData(new[] { 155.460, 155.475, 155.490 })]
    [InlineData(new[] { 462.5625, 462.5875, 462.6125 })]
    public void NudgedCentre_KeepsEveryChannelInTheWindow(double[] frequencies)
    {
        var channels = At(frequencies);
        double center = Center(channels);

        Assert.All(channels, c =>
            Assert.True(Math.Abs(c.Frequency - center) <= 1.2 + 1e-9,
                $"{c.AlphaTag} fell {Math.Abs(c.Frequency - center):F3} MHz from centre {center:F3}"));
        Assert.All(channels, c =>
            Assert.True(Math.Abs(c.Frequency - center) > 0.005,
                $"{c.AlphaTag} landed on the DC spike at centre {center:F3}"));
    }
}
