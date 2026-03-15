using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using OpenScanner.Server.Models;
using OpenScanner.Server.Devices;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Services;

namespace OpenScanner.Tests.Devices;

/// <summary>
/// Tests for the two-mode scan algorithm: FastScan (≤2.4 MHz span) vs FrequencyHop (>2.4 MHz span).
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
    public void CalculateScanBanks_TightCluster2MHz_ReturnsSingleFastScanBank()
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
    public void CalculateScanBanks_Wide5MHz_ReturnsOneFrequencyHopBankPerChannel()
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

        // 5 MHz span → FrequencyHop: one bank per channel
        Assert.Equal(6, banks.Count);
        Assert.All(banks, b => Assert.Equal(ScanMode.FrequencyHop, b.Mode));
        Assert.All(banks, b => Assert.Single(b.Frequencies));
        Assert.All(banks, b => Assert.Equal(1500, b.DwellTimeMs));
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

        // 2.5 MHz span → FrequencyHop: one bank per channel
        Assert.Equal(2, banks.Count);
        Assert.All(banks, b => Assert.Equal(ScanMode.FrequencyHop, b.Mode));
        Assert.Equal(155.0 + 0.25, banks[0].CenterFrequency);
        Assert.Equal(157.5 + 0.25, banks[1].CenterFrequency);
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
        Assert.Equal(ScanMode.FastScan, banks[0].Mode);
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
    public void CalculateScanBanks_RealWorldExample_WideSpanUsesFrequencyHop()
    {
        var device = CreateRtlDevice();
        // Total span 158.4 - 155.0325 = 3.37 MHz > 2.4 MHz → FrequencyHop, one bank per channel
        var channels = new List<Channel>
        {
            new() { Frequency = 155.0325, AlphaTag = "Ch1", Mode = "NFM" },
            new() { Frequency = 155.8875, AlphaTag = "Ch2", Mode = "NFM" },
            new() { Frequency = 158.4,    AlphaTag = "Ch3", Mode = "NFM" }
        };

        var banks = device.CalculateScanBanksForTesting(channels);

        Assert.Equal(3, banks.Count);
        Assert.All(banks, b => Assert.Equal(ScanMode.FrequencyHop, b.Mode));
        Assert.All(banks, b => Assert.Single(b.Frequencies));
    }
}
