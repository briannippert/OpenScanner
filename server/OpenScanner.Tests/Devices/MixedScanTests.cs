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
        Assert.Equal(0, banks[0].DwellTimeMs);
    }

    [Fact]
    public void CalculateScanBanks_Wide5MHz_ReturnsFrequencyHop()
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
        foreach (var bank in banks)
        {
            Assert.Equal(ScanMode.FrequencyHop, bank.Mode);
            Assert.Equal(2000, bank.DwellTimeMs);
        }
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
    public void CalculateScanBanks_JustOver2_4MHz_ReturnsFrequencyHop()
    {
        var device = CreateRtlDevice();
        var channels = new List<Channel>
        {
            new() { Frequency = 155.0, AlphaTag = "Ch1", Mode = "NFM" },
            new() { Frequency = 157.5, AlphaTag = "Ch2", Mode = "NFM" }
        };

        var banks = device.CalculateScanBanksForTesting(channels);

        Assert.NotEmpty(banks);
        Assert.Contains(banks, b => b.Mode == ScanMode.FrequencyHop);
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
    public void CalculateScanBanks_FrequencyHopSubclusters_EachUnder0_5MHz()
    {
        var device = CreateRtlDevice();
        // 5 contiguous channels spanning 5 MHz - will be broken into 0.5 MHz sub-clusters
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

        Assert.True(banks.Count > 1);
        foreach (var bank in banks)
        {
            if (bank.Frequencies.Count > 1)
            {
                var spread = bank.Frequencies.Max() - bank.Frequencies.Min();
                Assert.True(spread <= 0.55, $"Sub-cluster spread {spread:F2} MHz exceeds 0.5 MHz threshold");
            }
        }
    }
}
