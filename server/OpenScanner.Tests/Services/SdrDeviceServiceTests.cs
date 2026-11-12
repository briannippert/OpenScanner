using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenScanner.Server;
using OpenScanner.Server.Devices;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests.Services;

/// <summary>
/// Registration of attached dongles against a real SQLite database, so the schema,
/// migration and upsert are exercised together.
/// </summary>
public class SdrDeviceServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"openscanner_sdr_{Guid.NewGuid()}.db");
    private readonly Database _db;
    private readonly Mock<IDeviceEnumerator> _enumerator = new();
    private readonly SdrDeviceService _service;

    public SdrDeviceServiceTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = $"Data Source={_dbPath}" })
            .Build();
        _db = new Database(config, NullLogger<Database>.Instance);
        _service = new SdrDeviceService(_db, _enumerator.Object, NullLogger<SdrDeviceService>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private void Attach(params SdrDeviceInfo[] devices) =>
        _enumerator.Setup(e => e.EnumerateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(devices);

    private static SdrDeviceInfo Dongle(int index, string serial) =>
        new(index, "Realtek", "RTL2838UHIDIR", serial, $"sn:{serial}", true);

    /// <summary>
    /// Upgrading from the single-SDR settings must not change how the existing dongle runs:
    /// its gain becomes every gain class, and its measured ppm stays with it. A second dongle
    /// has its own crystal, so it starts uncorrected.
    /// </summary>
    [Fact]
    public async Task NewDevices_AreSeededFromTheLegacySettings()
    {
        await _db.SetSettingAsync("SdrGain", "32.8");
        await _db.SetSettingAsync("SdrPpm", "-41");
        Attach(Dongle(0, "A"), Dongle(1, "B"));

        var devices = await _service.SyncAsync();

        Assert.Equal(2, devices.Count);
        var a = devices[0].Config;
        Assert.Equal(("sn:A", "SDR 1", -41.0), (a.Id, a.Label, a.Ppm));
        Assert.Equal((32.8, 32.8, 32.8), (a.GainAnalog, a.GainDigital, a.GainMixed));
        var b = devices[1].Config;
        Assert.Equal(("sn:B", "SDR 2", 0.0), (b.Id, b.Label, b.Ppm));
        Assert.Equal(32.8, b.GainDigital);
        Assert.All(devices, d => Assert.True(d.Usable));
    }

    [Fact]
    public async Task UserSettings_SurviveAResync()
    {
        Attach(Dongle(0, "A"));
        var first = (await _service.SyncAsync())[0].Config;
        first.Label = "Roof";
        first.GainDigital = 28;
        first.Enabled = false;
        await _db.UpsertSdrDeviceAsync(first);

        var again = (await _service.SyncAsync())[0];

        Assert.Equal(("Roof", 28.0, false), (again.Config.Label, again.Config.GainDigital, again.Config.Enabled));
        Assert.False(again.Usable);
    }

    /// <summary>An unplugged dongle keeps its settings and shows up as not attached.</summary>
    [Fact]
    public async Task UnpluggedDevice_IsStillListed()
    {
        Attach(Dongle(0, "A"), Dongle(1, "B"));
        await _service.SyncAsync();

        Attach(Dongle(0, "B"));
        var devices = await _service.SyncAsync();

        Assert.Null(devices.Single(d => d.Config.Id == "sn:A").Attached);
        Assert.Equal(0, devices.Single(d => d.Config.Id == "sn:B").Attached!.Index);
    }

    [Fact]
    public async Task AssignmentMode_DefaultsToAutoAndRoundTrips()
    {
        Assert.Equal(SdrAssignmentMode.Auto, await _service.GetAssignmentModeAsync());
        await _service.SetAssignmentModeAsync(SdrAssignmentMode.Manual);
        Assert.Equal(SdrAssignmentMode.Manual, await _service.GetAssignmentModeAsync());
    }

    /// <summary>
    /// Editing a channel through the ordinary update must not clear its SDR pin: older
    /// clients don't send the field at all.
    /// </summary>
    [Fact]
    public async Task ChannelPin_SurvivesAnOrdinaryChannelUpdate()
    {
        var id = await _db.AddChannelAsync(new Channel(155.0, "A", "a"));
        await _db.SetChannelSdrDeviceAsync(new[] { id }, "sn:A");

        await _db.UpdateChannelAsync(new Channel(155.0, "Renamed", "a") { Id = id });

        var ch = Assert.Single(await _db.GetAllChannelsAsync());
        Assert.Equal(("Renamed", "sn:A"), (ch.AlphaTag, ch.SdrDeviceId));
    }

    [Theory]
    [InlineData("P25", GainClass.Digital)]
    [InlineData("dmr", GainClass.Digital)]
    [InlineData("NFM", GainClass.Analog)]
    [InlineData("AM", GainClass.Analog)]
    [InlineData(null, GainClass.Analog)]
    public void GainClass_FollowsTheMode(string? mode, GainClass expected)
    {
        Assert.Equal(expected, GainClasses.ForMode(mode));
    }

    [Fact]
    public void GainClass_OfAGroup_IsMixedOnlyWhenBothArePresent()
    {
        var p25 = new Channel { Mode = "P25" };
        var nfm = new Channel { Mode = "NFM" };
        Assert.Equal(GainClass.Digital, GainClasses.ForChannels(new[] { p25, p25 }));
        Assert.Equal(GainClass.Analog, GainClasses.ForChannels(new[] { nfm }));
        Assert.Equal(GainClass.Mixed, GainClasses.ForChannels(new[] { nfm, p25 }));
    }
}
