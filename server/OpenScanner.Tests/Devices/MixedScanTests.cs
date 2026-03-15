using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using OpenScanner.Server.Models;
using OpenScanner.Server.Devices;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Services;

namespace OpenScanner.Tests.Devices;

/// <summary>
/// Tests for mixed scan optimization (FastScan vs FrequencyHop modes).
/// </summary>
public class MixedScanTests
{
    private RtlDevice CreateRtlDevice()
    {
        var db = new Mock<IDatabase>();
        var logger = new Mock<ILogger<RtlDevice>>();
        var gps = new Mock<GpsService>(new Mock<ILogger<GpsService>>().Object);
        var toneDetector = new Mock<ToneDetector>(db.Object, new Mock<ILogger<ToneDetector>>().Object);
        var decoderFactory = new Mock<IDecoderFactory>();
        var transcription = new Mock<ITranscriptionService>();
        var recording = new Mock<IRecordingService>();
        var channelService = new Mock<IChannelService>();

        channelService.Setup(x => x.Channels).Returns(new List<Channel>());

        return new RtlDevice(
            db.Object,
            logger.Object,
            gps.Object,
            toneDetector.Object,
            decoderFactory.Object,
            transcription.Object,
            recording.Object,
            channelService.Object
        );
    }

    [Fact]
    public void CalculateScanBanks_TightCluster2MHz_ReturnsFastScan()
    {
        var device = CreateRtlDevice();
        var channels = new List<Channel>
        {
            new() { Frequency = 155.0, AlphaTag = "Ch1", Mode = "NFM" },
            new() { Frequency = 155.5, AlphaTag = "Ch2", Mode = "NFM" },
            new() { Frequency = 156.0, AlphaTag = "Ch3", Mode = "NFM" },
            new() { Frequency = 156.5, AlphaTag = "Ch4", Mode = "NFM" },
            new() { Frequency = 157.0, AlphaTag = "Ch5", Mode = "NFM" }
        };

        var banks = device.CalculateScanBanksForTesting(channels);

        Assert.Single(banks);
        Assert.Equal(ScanMode.FastScan, banks[0].Mode);
        Assert.Equal(2.0, banks[0].SpreadMHz);
        Assert.Equal(5, banks[0].Frequencies.Count);
        Assert.Equal(1500, banks[0].DwellTimeMs);
    }

    [Fact]
    public void CalculateScanBanks_Wide5MHz_ReturnsMultipleFastScanBanks()
    {
        var device = CreateRtlDevice();
        var channels = new List<Channel>
        {
            new() { Frequency = 150.0, AlphaTag = "Ch1", Mode = "NFM" },
            new() { Frequency = 151.0, AlphaTag = "Ch2", Mode = "NFM" },
            new() { Frequency = 152.0, AlphaTag = "Ch3", Mode = "NFM" },
            new() { Frequency = 153.0, AlphaTag = "Ch4", Mode = "NFM" },
            new() { Frequency = 154.0, AlphaTag = "Ch5", Mode = "NFM" },
            new() { Frequency = 155.0, AlphaTag = "Ch6", Mode = "NFM" }
        };

        var banks = device.CalculateScanBanksForTesting(channels);

        Assert.True(banks.Count > 1, "5 MHz span should create multiple banks");
        Assert.All(banks, b => Assert.Equal(ScanMode.FastScan, b.Mode));
        Assert.All(banks, b => Assert.Equal(1500, b.DwellTimeMs));
        Assert.All(banks, b => Assert.True(b.SpreadMHz <= 2.4 + 1e-9, $"Bank spread {b.SpreadMHz:F2} MHz exceeds 2.4 MHz window"));
    }

    [Fact]
    public void CalculateScanBanks_SingleChannel_ReturnsFastScan()
    {
        var device = CreateRtlDevice();
        var channels = new List<Channel>
        {
            new() { Frequency = 155.0, AlphaTag = "Ch1", Mode = "NFM" }
        };

        var banks = device.CalculateScanBanksForTesting(channels);

        Assert.Single(banks);
        Assert.Equal(ScanMode.FastScan, banks[0].Mode);
        Assert.Equal(0.0, banks[0].SpreadMHz);
    }

    [Fact]
    public void CalculateScanBanks_Exactly2_4MHz_ReturnsFastScan()
    {
        var device = CreateRtlDevice();
        var channels = new List<Channel>
        {
            new() { Frequency = 155.0, AlphaTag = "Ch1", Mode = "NFM" },
            new() { Frequency = 157.4, AlphaTag = "Ch2", Mode = "NFM" }
        };

        var banks = device.CalculateScanBanksForTesting(channels);

        Assert.Single(banks);
        Assert.Equal(ScanMode.FastScan, banks[0].Mode);
        Assert.Equal(2.4, banks[0].SpreadMHz, precision: 6);
    }

    [Fact]
    public void CalculateScanBanks_JustOver2_4MHz_ReturnsTwoFastScanBanks()
    {
        var device = CreateRtlDevice();
        var channels = new List<Channel>
        {
            new() { Frequency = 155.0, AlphaTag = "Ch1", Mode = "NFM" },
            new() { Frequency = 157.5, AlphaTag = "Ch2", Mode = "NFM" }
        };

        var banks = device.CalculateScanBanksForTesting(channels);

        // 2.5 MHz span exceeds the 2.4 MHz window → two separate FastScan banks
        Assert.Equal(2, banks.Count);
        Assert.All(banks, b => Assert.Equal(ScanMode.FastScan, b.Mode));
        Assert.Equal(155.0, banks[0].Frequencies[0]);
        Assert.Equal(157.5, banks[1].Frequencies[0]);
    }

    [Fact]
    public void CalculateScanBanks_TwoDisjointFastScanGroups_ReturnsTwoBanks()
    {
        var device = CreateRtlDevice();
        var channels = new List<Channel>
        {
            // Group 1: 155.0-156.5 MHz (1.5 MHz - fits in 2.4 MHz)
            new() { Frequency = 155.0, AlphaTag = "G1-Ch1", Mode = "NFM" },
            new() { Frequency = 155.5, AlphaTag = "G1-Ch2", Mode = "NFM" },
            new() { Frequency = 156.0, AlphaTag = "G1-Ch3", Mode = "NFM" },
            new() { Frequency = 156.5, AlphaTag = "G1-Ch4", Mode = "NFM" },
            // Gap > 2 MHz
            // Group 2: 160.0-160.8 MHz (0.8 MHz - fits in 2.4 MHz)
            new() { Frequency = 160.0, AlphaTag = "G2-Ch1", Mode = "NFM" },
            new() { Frequency = 160.4, AlphaTag = "G2-Ch2", Mode = "NFM" },
            new() { Frequency = 160.8, AlphaTag = "G2-Ch3", Mode = "NFM" }
        };

        var banks = device.CalculateScanBanksForTesting(channels);

        Assert.Equal(2, banks.Count);
        Assert.All(banks, b => Assert.Equal(ScanMode.FastScan, b.Mode));
    }

    [Fact]
    public void CalculateScanBanks_CenterFrequency_IsCorrectlyCalculated()
    {
        var device = CreateRtlDevice();
        var channels = new List<Channel>
        {
            new() { Frequency = 155.0, AlphaTag = "Ch1", Mode = "NFM" },
            new() { Frequency = 157.0, AlphaTag = "Ch2", Mode = "NFM" }
        };

        var banks = device.CalculateScanBanksForTesting(channels);

        Assert.Single(banks);
        Assert.Equal(156.0, banks[0].CenterFrequency, precision: 3);
    }

    [Fact]
    public void CalculateScanBanks_EmptyList_ReturnsEmpty()
    {
        var device = CreateRtlDevice();
        var banks = device.CalculateScanBanksForTesting(new List<Channel>());
        Assert.Empty(banks);
    }

    [Fact]
    public void CalculateScanBanks_HybridScan_SubBanksEachWithin2_4MHz()
    {
        var device = CreateRtlDevice();
        // 9 channels spanning 5.2 MHz — all contiguous, so one group split into sub-banks
        var channels = new List<Channel>
        {
            new() { Frequency = 150.0, AlphaTag = "Ch1", Mode = "NFM" },
            new() { Frequency = 150.2, AlphaTag = "Ch2", Mode = "NFM" },
            new() { Frequency = 150.8, AlphaTag = "Ch3", Mode = "NFM" },
            new() { Frequency = 151.5, AlphaTag = "Ch4", Mode = "NFM" },
            new() { Frequency = 152.2, AlphaTag = "Ch5", Mode = "NFM" },
            new() { Frequency = 153.0, AlphaTag = "Ch6", Mode = "NFM" },
            new() { Frequency = 154.0, AlphaTag = "Ch7", Mode = "NFM" },
            new() { Frequency = 154.6, AlphaTag = "Ch8", Mode = "NFM" },
            new() { Frequency = 155.2, AlphaTag = "Ch9", Mode = "NFM" }
        };

        var banks = device.CalculateScanBanksForTesting(channels);

        Assert.True(banks.Count > 1, "5.2 MHz span should produce multiple banks");
        Assert.All(banks, b => Assert.Equal(ScanMode.FastScan, b.Mode));
        Assert.All(banks, b => Assert.True(b.SpreadMHz <= 2.4 + 1e-9,
            $"Sub-bank spread {b.SpreadMHz:F2} MHz exceeds 2.4 MHz window"));
    }

    [Fact]
    public void CalculateScanBanks_HybridGroupExample_TwoFastScanPlusOneSingle()
    {
        var device = CreateRtlDevice();
        // Real-world hybrid: 155.0325 + 155.8875 fit in 2.4 MHz window, 158.4 is isolated
        var channels = new List<Channel>
        {
            new() { Frequency = 155.0325, AlphaTag = "Ch1", Mode = "NFM" },
            new() { Frequency = 155.8875, AlphaTag = "Ch2", Mode = "NFM" },
            new() { Frequency = 158.4,    AlphaTag = "Ch3", Mode = "NFM" }
        };

        var banks = device.CalculateScanBanksForTesting(channels);

        // 155.0325-155.8875 span is 0.855 MHz → one FastScan bank
        // 158.4 is > 3 MHz away → separate group → second FastScan bank
        Assert.Equal(2, banks.Count);
        Assert.All(banks, b => Assert.Equal(ScanMode.FastScan, b.Mode));
        Assert.Equal(2, banks[0].Frequencies.Count);  // 155.0325 + 155.8875
        Assert.Single(banks[1].Frequencies);            // 158.4
    }
}
