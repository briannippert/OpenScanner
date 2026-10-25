using OpenScanner.Server.Devices;
using Xunit;

namespace OpenScanner.Tests.Devices;

/// <summary>
/// When the dongle drops off USB the scanner keeps relaunching rtl_sdr until it comes back.
/// The backoff must start fast (a brief glitch should cost seconds, not minutes) but must not
/// hammer a missing device forever, and it must never stop retrying.
/// </summary>
public class ReconnectDelayTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(4, 16)]
    [InlineData(5, 30)]
    [InlineData(1000, 30)]
    public void Backoff_doubles_then_caps_at_30s(int failures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RtlDevice.ReconnectDelay(failures));
    }
}
