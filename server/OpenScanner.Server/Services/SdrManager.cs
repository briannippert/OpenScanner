using System.Globalization;
using OpenScanner.Server.Devices;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

/// <summary>
/// Everything the radio needs to know about its SDRs at one moment.
/// </summary>
/// <param name="Devices">Every known dongle, attached or not, in display order.</param>
/// <param name="Mode">Auto or Manual assignment.</param>
/// <param name="Plan">Which SDR carries each channel.</param>
/// <param name="DeviceIndexes">The current librtlsdr index of each planned SDR, by id; null for the default device.</param>
public record SdrSnapshot(
    IReadOnlyList<SdrDevice> Devices,
    SdrAssignmentMode Mode,
    SdrPlan Plan,
    IReadOnlyDictionary<string, int?> DeviceIndexes)
{
    /// <summary>An empty snapshot, before the first plan.</summary>
    public static readonly SdrSnapshot Empty = new(
        Array.Empty<SdrDevice>(), SdrAssignmentMode.Auto,
        new SdrPlan(Array.Empty<DeviceAssignment>(), Array.Empty<Channel>(), Array.Empty<Channel>()),
        new Dictionary<string, int?>());
}

/// <summary>
/// Owns the SDR set and the channel plan. It re-plans when the channels change, when a
/// dongle's settings or the assignment mode change, and when dongles are plugged in or
/// removed, and announces each new plan through <see cref="PlanChanged"/>.
/// </summary>
public class SdrManager : BackgroundService
{
    /// <summary>
    /// The id of the stand-in device used when no dongle can be listed (none plugged in yet,
    /// or rtl_test missing). It runs on librtlsdr's default device with the legacy global gain
    /// and ppm, which is exactly how OpenScanner behaved before multi-SDR support.
    /// </summary>
    public const string DefaultDeviceId = "default";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly SdrDeviceService _devices;
    private readonly IChannelService _channels;
    private readonly IDatabase _db;
    private readonly ILogger<SdrManager> _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _rescanSignal = new(0, 1);

    private IReadOnlyList<SdrDevice> _deviceList = Array.Empty<SdrDevice>();
    private SdrAssignmentMode _mode = SdrAssignmentMode.Auto;
    private SdrSnapshot _current = SdrSnapshot.Empty;
    private string _attachedSignature = string.Empty;

    /// <summary>Raised with each new plan.</summary>
    public event Action<SdrSnapshot>? PlanChanged;

    /// <summary>Initializes a new instance of the <see cref="SdrManager"/> class.</summary>
    public SdrManager(SdrDeviceService devices, IChannelService channels, IDatabase db, ILogger<SdrManager> logger)
    {
        _devices = devices;
        _channels = channels;
        _db = db;
        _logger = logger;
        _channels.ChannelsChanged += () =>
        {
            if (_ready.Task.IsCompleted) Replan();
        };
    }

    /// <summary>The current plan.</summary>
    public SdrSnapshot Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>Completes once the first plan exists.</summary>
    public Task WhenReady => _ready.Task;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _mode = await _devices.GetAssignmentModeAsync();
            await RefreshDevicesAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not list SDRs; falling back to the default device");
            Replan();
        }
        finally
        {
            _ready.TrySetResult();
        }

        // Dongles can be plugged in or pulled at any time. Listing them doesn't open them, so
        // polling is safe while captures run. A worker that loses its dongle asks for an
        // immediate rescan via RequestRescan.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _rescanSignal.WaitAsync(PollInterval, stoppingToken);
                await RefreshDevicesAsync(stoppingToken, onlyIfAttachedChanged: true);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "SDR rescan failed"); }
        }
    }

    /// <summary>Asks for the attached dongles to be listed again soon, e.g. after a capture lost its device.</summary>
    public void RequestRescan()
    {
        try { _rescanSignal.Release(); } catch (SemaphoreFullException) { /* already pending */ }
    }

    /// <summary>Lists the attached dongles and re-plans.</summary>
    /// <param name="token">Cancellation.</param>
    /// <param name="onlyIfAttachedChanged">Skip the re-plan when the same dongles sit at the same indexes.</param>
    public async Task RefreshDevicesAsync(CancellationToken token = default, bool onlyIfAttachedChanged = false)
    {
        await _refreshLock.WaitAsync(token);
        try
        {
            var devices = await _devices.SyncAsync(token);
            var signature = string.Join(";", devices
                .Where(d => d.Attached != null)
                .Select(d => $"{d.Config.Id}@{d.Attached!.Index}"));

            bool changed;
            lock (_lock)
            {
                changed = signature != _attachedSignature;
                _attachedSignature = signature;
                _deviceList = devices;
            }

            if (changed)
                _logger.LogInformation("Attached SDRs: {Devices}", signature.Length == 0 ? "none" : signature);
            if (changed || !onlyIfAttachedChanged) Replan();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Re-plans with the current channels, devices and mode, and announces the result.</summary>
    public void Replan()
    {
        SdrSnapshot snapshot;
        lock (_lock)
        {
            var (configs, indexes) = PlannedDevices(_deviceList);
            var channels = _channels.Channels.Where(c => !c.Avoid).ToList();
            var plan = SdrPlanner.Plan(channels, configs, _mode, _current.Plan);
            snapshot = new SdrSnapshot(_deviceList, _mode, plan, indexes);
            _current = snapshot;
        }

        _logger.LogInformation(
            "SDR plan ({Mode}): {Fast} of {Total} channels fast-scanned across {Sdrs} SDR(s){Unscanned}",
            snapshot.Mode, snapshot.Plan.FastScannedCount, snapshot.Plan.ScannedCount + snapshot.Plan.Unscanned.Count,
            snapshot.Plan.Devices.Count(d => d.Role != SdrRole.Idle),
            snapshot.Plan.Unscanned.Count > 0 ? $", {snapshot.Plan.Unscanned.Count} unscanned" : "");

        PlanChanged?.Invoke(snapshot);
    }

    /// <summary>
    /// The SDRs the planner may use and their current indexes. With no dongle listed at all,
    /// the stand-in default device keeps the scanner running exactly as it did with one SDR.
    /// </summary>
    private (List<SdrDeviceConfig> Configs, Dictionary<string, int?> Indexes) PlannedDevices(IReadOnlyList<SdrDevice> devices)
    {
        if (devices.All(d => d.Attached == null))
        {
            var fallback = LegacyDefaultDevice();
            return (new List<SdrDeviceConfig> { fallback }, new Dictionary<string, int?> { [fallback.Id] = null });
        }

        var usable = devices.Where(d => d.Usable).ToList();
        return (usable.Select(d => d.Config).ToList(),
                usable.ToDictionary(d => d.Config.Id, d => (int?)d.Attached!.Index));
    }

    private SdrDeviceConfig LegacyDefaultDevice()
    {
        double Read(string key) =>
            double.TryParse(_db.GetSettingAsync(key).GetAwaiter().GetResult(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var gain = Read("SdrGain");
        return new SdrDeviceConfig
        {
            Id = DefaultDeviceId,
            Label = "SDR",
            Ppm = Read("SdrPpm"),
            GainAnalog = gain,
            GainDigital = gain,
            GainMixed = gain,
        };
    }

    /// <summary>Plans with hypothetical pins without applying anything, for live UI feedback.</summary>
    /// <param name="mode">The mode to plan in.</param>
    /// <param name="pins">Channel id → SDR id (null to unpin), overriding the stored pins.</param>
    public SdrPlan Preview(SdrAssignmentMode mode, IReadOnlyDictionary<int, string?>? pins)
    {
        IReadOnlyList<SdrDevice> devices;
        SdrPlan previous;
        lock (_lock)
        {
            devices = _deviceList;
            previous = _current.Plan;
        }

        var channels = _channels.Channels
            .Where(c => !c.Avoid)
            .Select(c =>
            {
                if (pins == null || c.Id is not int id || !pins.TryGetValue(id, out var pin)) return c;
                var copy = c.MemberwiseCloneChannel();
                copy.SdrDeviceId = pin;
                return copy;
            })
            .ToList();

        return SdrPlanner.Plan(channels, PlannedDevices(devices).Configs, mode, previous);
    }

    /// <summary>Changes the assignment mode and re-plans.</summary>
    public async Task SetModeAsync(SdrAssignmentMode mode)
    {
        await _devices.SetAssignmentModeAsync(mode);
        lock (_lock) _mode = mode;
        Replan();
    }

    /// <summary>
    /// Edits a dongle's stored settings and re-plans. Returns the updated settings, or null
    /// when the dongle is unknown.
    /// </summary>
    public async Task<SdrDeviceConfig?> UpdateDeviceAsync(string id, Action<SdrDeviceConfig> edit)
    {
        SdrDeviceConfig? current;
        lock (_lock) current = _deviceList.FirstOrDefault(d => d.Config.Id == id)?.Config;
        if (current == null) return null;

        // Edit a copy: the running plan holds the current object, and the change has to be
        // visible as a difference between the old plan and the new one.
        var config = current.Clone();
        edit(config);
        config.Id = current.Id;
        await _db.UpsertSdrDeviceAsync(config);
        ReplaceConfigs(new[] { config });
        Replan();
        return config;
    }

    /// <summary>
    /// Applies the legacy single gain to every dongle (all gain classes) and to the stored
    /// legacy setting, so the original gain control keeps working on any number of SDRs.
    /// </summary>
    public async Task SetGainForAllAsync(double db)
    {
        await _db.SetSettingAsync("SdrGain", db.ToString("0.##", CultureInfo.InvariantCulture));
        var updated = new List<SdrDeviceConfig>();
        foreach (var d in SnapshotDevices())
        {
            var config = d.Config.Clone();
            config.GainAnalog = config.GainDigital = config.GainMixed = db;
            await _db.UpsertSdrDeviceAsync(config);
            updated.Add(config);
        }
        ReplaceConfigs(updated);
        Replan();
    }

    /// <summary>
    /// Applies the legacy single ppm. It is a property of one crystal, so it goes to every
    /// dongle only when there is just one; with several it is stored for the default device.
    /// </summary>
    public async Task SetPpmForAllAsync(double ppm)
    {
        await _db.SetSettingAsync("SdrPpm", ppm.ToString("0.##", CultureInfo.InvariantCulture));
        var devices = SnapshotDevices();
        if (devices.Count == 1)
        {
            var config = devices[0].Config.Clone();
            config.Ppm = ppm;
            await _db.UpsertSdrDeviceAsync(config);
            ReplaceConfigs(new[] { config });
        }
        Replan();
    }

    private void ReplaceConfigs(IEnumerable<SdrDeviceConfig> configs)
    {
        var byId = configs.ToDictionary(c => c.Id);
        lock (_lock)
        {
            _deviceList = _deviceList
                .Select(d => byId.TryGetValue(d.Config.Id, out var c) ? d with { Config = c } : d)
                .ToList();
        }
    }

    private IReadOnlyList<SdrDevice> SnapshotDevices()
    {
        lock (_lock) return _deviceList;
    }
}

internal static class ChannelCloneExtensions
{
    /// <summary>A shallow copy of a channel, so a preview can re-pin it without touching the live list.</summary>
    public static Channel MemberwiseCloneChannel(this Channel c) => new()
    {
        Id = c.Id, Frequency = c.Frequency, AlphaTag = c.AlphaTag, Description = c.Description, Mode = c.Mode,
        Type = c.Type, Tone = c.Tone, Tag = c.Tag, License = c.License, Lat = c.Lat, Lon = c.Lon, Range = c.Range,
        Avoid = c.Avoid, DmrSlot = c.DmrSlot, DmrColorCode = c.DmrColorCode, DmrTalkgroup = c.DmrTalkgroup,
        SdrDeviceId = c.SdrDeviceId,
    };
}
