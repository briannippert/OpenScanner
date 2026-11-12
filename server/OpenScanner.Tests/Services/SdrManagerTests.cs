using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenScanner.Server;
using OpenScanner.Server.Devices;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests.Services;

public class SdrManagerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"openscanner_mgr_{Guid.NewGuid()}.db");
    private readonly Database _db;
    private readonly Mock<IDeviceEnumerator> _enumerator = new();
    private readonly Mock<IChannelService> _channels = new();
    private List<Channel> _channelList = new();
    private readonly SdrManager _manager;
    private readonly List<SdrSnapshot> _published = new();

    public SdrManagerTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = $"Data Source={_dbPath}" })
            .Build();
        _db = new Database(config, NullLogger<Database>.Instance);
        _channels.Setup(c => c.Channels).Returns(() => _channelList);
        Attach();
        _manager = new SdrManager(
            new SdrDeviceService(_db, _enumerator.Object, NullLogger<SdrDeviceService>.Instance),
            _channels.Object, _db, NullLogger<SdrManager>.Instance);
        _manager.PlanChanged += s => _published.Add(s);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private void Attach(params string[] serials) =>
        _enumerator.Setup(e => e.EnumerateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(serials.Select((s, i) => new SdrDeviceInfo(i, "Realtek", "RTL2838UHIDIR", s, $"sn:{s}", true)).ToList());

    private static Channel Ch(int id, double f, string mode = "NFM") => new() { Id = id, Frequency = f, Mode = mode, AlphaTag = $"{f}" };

    /// <summary>
    /// With no dongle listed (none plugged in, or rtl_test missing) the scanner still runs on
    /// librtlsdr's default device with the legacy gain, exactly as it did before.
    /// </summary>
    [Fact]
    public async Task NoDevicesListed_FallsBackToTheDefaultDevice()
    {
        await _db.SetSettingAsync("SdrGain", "33");
        _channelList = new() { Ch(1, 155.0) };

        await _manager.RefreshDevicesAsync();

        var a = Assert.Single(_manager.Current.Plan.Devices);
        Assert.Equal(SdrManager.DefaultDeviceId, a.Device.Id);
        Assert.Equal(33, a.GainFor());
        Assert.Null(_manager.Current.DeviceIndexes[SdrManager.DefaultDeviceId]);
    }

    [Fact]
    public async Task AttachedDevices_ArePlannedWithTheirIndexes()
    {
        Attach("A", "B");
        _channelList = new() { Ch(1, 155.0), Ch(2, 460.0) };

        await _manager.RefreshDevicesAsync();

        Assert.Equal(2, _manager.Current.Plan.FastScannedCount);
        Assert.Equal(1, _manager.Current.DeviceIndexes["sn:B"]);
    }

    [Fact]
    public async Task UnchangedHardware_DoesNotReplanOnPoll()
    {
        Attach("A");
        await _manager.RefreshDevicesAsync();
        var count = _published.Count;

        await _manager.RefreshDevicesAsync(onlyIfAttachedChanged: true);
        Assert.Equal(count, _published.Count);

        Attach("A", "B");
        await _manager.RefreshDevicesAsync(onlyIfAttachedChanged: true);
        Assert.Equal(count + 1, _published.Count);
    }

    [Fact]
    public async Task DisabledDevice_IsNotPlanned()
    {
        Attach("A", "B");
        _channelList = new() { Ch(1, 155.0), Ch(2, 460.0) };
        await _manager.RefreshDevicesAsync();

        await _manager.UpdateDeviceAsync("sn:B", c => c.Enabled = false);

        Assert.Equal(new[] { "sn:A" }, _manager.Current.Plan.Devices.Select(d => d.Device.Id));
    }

    /// <summary>
    /// The plan holding the old settings must still show the old value after an edit, or
    /// the coordinator could never see that anything changed.
    /// </summary>
    [Fact]
    public async Task UpdateDevice_DoesNotMutateTheRunningPlan()
    {
        Attach("A");
        _channelList = new() { Ch(1, 155.0, "P25") };
        await _manager.RefreshDevicesAsync();
        var before = _manager.Current.Plan.Devices[0];

        await _manager.UpdateDeviceAsync("sn:A", c => c.GainDigital = 12);

        Assert.NotEqual(12, before.Device.GainDigital);
        Assert.Equal(12, _manager.Current.Plan.Devices[0].GainFor());
        Assert.Equal(12, (await _db.GetSdrDevicesAsync()).Single().GainDigital);
    }

    [Fact]
    public async Task UnknownDevice_UpdateReturnsNull()
    {
        await _manager.RefreshDevicesAsync();
        Assert.Null(await _manager.UpdateDeviceAsync("sn:NOPE", _ => { }));
    }

    [Fact]
    public async Task ModeChange_IsStoredAndReplanned()
    {
        Attach("A", "B");
        _channelList = new() { Ch(1, 155.0) };
        _channelList[0].SdrDeviceId = "sn:B";
        await _manager.RefreshDevicesAsync();
        Assert.Equal("sn:A", _manager.Current.Plan.OwnerOf(155.0)!.Device.Id);

        await _manager.SetModeAsync(SdrAssignmentMode.Manual);

        Assert.Equal("sn:B", _manager.Current.Plan.OwnerOf(155.0)!.Device.Id);
        Assert.Equal("manual", await _db.GetSettingAsync(SdrDeviceService.AssignmentModeKey));
    }

    /// <summary>A preview re-pins copies; the live channel list is untouched.</summary>
    [Fact]
    public async Task Preview_UsesProposedPinsWithoutApplyingThem()
    {
        Attach("A", "B");
        _channelList = new() { Ch(1, 155.0), Ch(2, 155.1) };
        await _manager.RefreshDevicesAsync();
        var published = _published.Count;

        var plan = _manager.Preview(SdrAssignmentMode.Manual, new Dictionary<int, string?> { [2] = "sn:B" });

        Assert.Equal("sn:B", plan.OwnerOf(155.1)!.Device.Id);
        Assert.Null(_channelList[1].SdrDeviceId);
        Assert.Equal(published, _published.Count);
    }

    [Fact]
    public async Task LegacyGain_AppliesToEveryDeviceAndClass()
    {
        Attach("A", "B");
        await _manager.RefreshDevicesAsync();

        await _manager.SetGainForAllAsync(25);

        Assert.All(await _db.GetSdrDevicesAsync(), d => Assert.Equal((25.0, 25.0, 25.0), (d.GainAnalog, d.GainDigital, d.GainMixed)));
        Assert.Equal("25", await _db.GetSettingAsync("SdrGain"));
    }

    [Fact]
    public async Task AvoidedChannels_AreNotPlanned()
    {
        Attach("A");
        _channelList = new() { Ch(1, 155.0), Ch(2, 155.1) };
        _channelList[1].Avoid = true;
        await _manager.RefreshDevicesAsync();
        Assert.Equal(1, _manager.Current.Plan.ScannedCount);
    }
}
