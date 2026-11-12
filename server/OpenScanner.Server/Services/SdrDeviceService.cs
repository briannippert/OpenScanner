using System.Globalization;
using OpenScanner.Server.Devices;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

/// <summary>An SDR dongle's stored settings, plus where it is attached right now (null if unplugged).</summary>
public record SdrDevice(SdrDeviceConfig Config, SdrDeviceInfo? Attached)
{
    /// <summary>Whether the dongle is attached and enabled, i.e. available to scan with.</summary>
    public bool Usable => Attached != null && Config.Enabled;
}

/// <summary>
/// Keeps the <c>sdr_devices</c> table in step with the dongles actually attached, and
/// stores the assignment mode.
/// </summary>
public class SdrDeviceService
{
    /// <summary>Settings key holding the <see cref="SdrAssignmentMode"/>.</summary>
    public const string AssignmentModeKey = "SdrAssignmentMode";

    private readonly IDatabase _db;
    private readonly IDeviceEnumerator _enumerator;
    private readonly ILogger<SdrDeviceService> _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="SdrDeviceService"/> class.</summary>
    public SdrDeviceService(IDatabase db, IDeviceEnumerator enumerator, ILogger<SdrDeviceService> logger)
    {
        _db = db;
        _enumerator = enumerator;
        _logger = logger;
    }

    /// <summary>
    /// Lists attached dongles, records any not seen before, and returns every known dongle
    /// (attached or not) in display order.
    /// </summary>
    public async Task<IReadOnlyList<SdrDevice>> SyncAsync(CancellationToken token = default)
    {
        var attached = await _enumerator.EnumerateAsync(token);

        await _syncLock.WaitAsync(token);
        try
        {
            var known = (await _db.GetSdrDevicesAsync()).ToDictionary(d => d.Id);
            var firstEver = known.Count == 0;

            foreach (var info in attached)
            {
                if (known.TryGetValue(info.Id, out var existing))
                {
                    // Refresh what the USB descriptor says, keep everything the user set.
                    if (existing.Serial != info.Serial || existing.Product != info.Product)
                    {
                        existing.Serial = info.Serial;
                        existing.Product = info.Product;
                        await _db.UpsertSdrDeviceAsync(existing);
                    }
                    continue;
                }

                var config = await NewDeviceAsync(info, seedPpm: firstEver, sortOrder: known.Count);
                firstEver = false;
                await _db.UpsertSdrDeviceAsync(config);
                known[config.Id] = config;
                _logger.LogInformation("Registered new SDR {Id} ({Product}) at index {Index}", config.Id, info.Product, info.Index);
            }

            if (attached.Any(a => !a.SerialIsUnique))
            {
                _logger.LogWarning(
                    "Some attached RTL-SDRs share a serial number, so they can only be told apart by USB " +
                    "port order. Give each a unique serial with: rtl_eeprom -d <index> -s <serial>");
            }

            var byId = attached.ToDictionary(a => a.Id);
            return known.Values
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Id, StringComparer.Ordinal)
                .Select(c => new SdrDevice(c, byId.GetValueOrDefault(c.Id)))
                .ToList();
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Settings for a dongle seen for the first time. Before multi-SDR support there was one
    /// global gain and ppm; a new dongle starts from that gain for every gain class so nothing
    /// changes until the user tunes it. The ppm is a property of one particular crystal, so it
    /// only carries over to the first dongle ever registered, the one it was measured on.
    /// </summary>
    private async Task<SdrDeviceConfig> NewDeviceAsync(SdrDeviceInfo info, bool seedPpm, int sortOrder)
    {
        var gain = await ReadNumberAsync("SdrGain") ?? 0;
        var ppm = seedPpm ? await ReadNumberAsync("SdrPpm") ?? 0 : 0;
        return new SdrDeviceConfig
        {
            Id = info.Id,
            Label = $"SDR {sortOrder + 1}",
            Serial = info.Serial,
            Product = info.Product,
            Enabled = true,
            Ppm = ppm,
            GainAnalog = gain,
            GainDigital = gain,
            GainMixed = gain,
            SortOrder = sortOrder,
        };
    }

    private async Task<double?> ReadNumberAsync(string key)
    {
        var raw = await _db.GetSettingAsync(key);
        return double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>The stored assignment mode; Auto when unset or unrecognised.</summary>
    public async Task<SdrAssignmentMode> GetAssignmentModeAsync()
    {
        var raw = await _db.GetSettingAsync(AssignmentModeKey);
        return Enum.TryParse<SdrAssignmentMode>(raw, ignoreCase: true, out var mode) ? mode : SdrAssignmentMode.Auto;
    }

    /// <summary>Stores the assignment mode.</summary>
    public Task SetAssignmentModeAsync(SdrAssignmentMode mode) =>
        _db.SetSettingAsync(AssignmentModeKey, mode.ToString().ToLowerInvariant());
}
