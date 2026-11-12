using OpenScanner.Server.Devices;
using Xunit;

namespace OpenScanner.Tests.Devices;

/// <summary>Parsing of librtlsdr's device listing, as printed by <c>rtl_test -d &lt;no-match&gt;</c>.</summary>
public class RtlDeviceEnumeratorTests
{
    [Fact]
    public void NoDevices_ParsesToEmpty()
    {
        Assert.Empty(RtlDeviceEnumerator.Parse("No supported devices found.\n"));
    }

    [Fact]
    public void SingleDevice_IsIdentifiedBySerial()
    {
        var devices = RtlDeviceEnumerator.Parse("""
            Found 1 device(s):
              0:  Realtek, RTL2838UHIDIR, SN: 00000001

            No matching devices found.
            """);

        var d = Assert.Single(devices);
        Assert.Equal(0, d.Index);
        Assert.Equal("Realtek", d.Vendor);
        Assert.Equal("RTL2838UHIDIR", d.Product);
        Assert.Equal("00000001", d.Serial);
        Assert.Equal("sn:00000001", d.Id);
        Assert.True(d.SerialIsUnique);
    }

    [Fact]
    public void ProductWithCommas_KeepsThemInTheProduct()
    {
        var d = Assert.Single(RtlDeviceEnumerator.Parse("  0:  Nooelec, NESDR SMArt v5, rev B, SN: 12345678\n"));
        Assert.Equal("Nooelec", d.Vendor);
        Assert.Equal("NESDR SMArt v5, rev B", d.Product);
        Assert.Equal("12345678", d.Serial);
    }

    /// <summary>
    /// Stock dongles all ship as 00000001. Shared serials can't tell the dongles apart, so
    /// they fall back to the USB index; a dongle with its own serial keeps it.
    /// </summary>
    [Fact]
    public void DuplicateSerials_FallBackToIndex()
    {
        var devices = RtlDeviceEnumerator.Parse("""
            Found 3 device(s):
              0:  Realtek, RTL2838UHIDIR, SN: 00000001
              1:  Realtek, RTL2838UHIDIR, SN: 00000001
              2:  RTLSDRBlog, Blog V4, SN: ROOF
            """);

        Assert.Equal(new[] { "idx:0", "idx:1", "sn:ROOF" }, devices.Select(d => d.Id));
        Assert.Equal(new[] { false, false, true }, devices.Select(d => d.SerialIsUnique));
    }

    [Fact]
    public void EmptySerial_FallsBackToIndex()
    {
        var d = Assert.Single(RtlDeviceEnumerator.Parse("  0:  Generic, RTL2832U, SN: \n"));
        Assert.Equal("idx:0", d.Id);
        Assert.False(d.SerialIsUnique);
    }

    [Fact]
    public void ToolNoiseAroundTheListing_IsIgnored()
    {
        var devices = RtlDeviceEnumerator.Parse("""
            Found 2 device(s):
              0:  Realtek, RTL2838UHIDIR, SN: A
              1:  Realtek, RTL2838UHIDIR, SN: B

            Using device 0: Generic RTL2832U OEM
            Found Rafael Micro R820T tuner
            Supported gain values (29): 0.0 0.9 1.4
            """);

        Assert.Equal(new[] { 0, 1 }, devices.Select(d => d.Index));
    }
}
