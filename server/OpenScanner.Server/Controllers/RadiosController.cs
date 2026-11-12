using Microsoft.AspNetCore.Mvc;
using OpenScanner.Server.Devices;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;

namespace OpenScanner.Server.Controllers;

/// <summary>
/// SDR dongles, how channels are divided between them, and each one's tuning.
/// </summary>
[ApiController]
[Route("api/radios")]
[Produces("application/json")]
public class RadiosController : ControllerBase
{
    private const double MaxGainDb = 60;
    private const double MaxPpm = 500;

    private readonly SdrManager _manager;
    private readonly IDatabase _db;
    private readonly IChannelService _channels;

    /// <summary>Initializes a new instance of the <see cref="RadiosController"/> class.</summary>
    public RadiosController(SdrManager manager, IDatabase db, IChannelService channels)
    {
        _manager = manager;
        _db = db;
        _channels = channels;
    }

    /// <summary>Every known dongle, the assignment mode and the current plan.</summary>
    [HttpGet("")]
    [ProducesResponseType(typeof(RadiosResponse), StatusCodes.Status200OK)]
    public RadiosResponse Get() => BuildResponse(_manager.Current);

    /// <summary>Lists the attached dongles again (after plugging one in) and re-plans.</summary>
    [HttpPost("rescan")]
    [ProducesResponseType(typeof(RadiosResponse), StatusCodes.Status200OK)]
    public async Task<RadiosResponse> Rescan(CancellationToken token)
    {
        await _manager.RefreshDevicesAsync(token);
        return BuildResponse(_manager.Current);
    }

    /// <summary>Edits one dongle's settings. Omitted fields are left as they are.</summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(RadioDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateRadioRequest request)
    {
        if (request.Label is { Length: > 64 }) return BadRequest("Label must be 64 characters or fewer.");
        foreach (var gain in new[] { request.GainAnalog, request.GainDigital, request.GainMixed })
            if (gain is < 0 or > MaxGainDb) return BadRequest($"Gain must be between 0 (AUTO) and {MaxGainDb} dB.");
        if (request.Ppm is < -MaxPpm or > MaxPpm) return BadRequest($"Frequency correction must be within ±{MaxPpm} ppm.");

        var updated = await _manager.UpdateDeviceAsync(id, c =>
        {
            if (request.Label != null) c.Label = request.Label.Trim();
            if (request.Enabled is bool enabled) c.Enabled = enabled;
            if (request.Ppm is double ppm) c.Ppm = ppm;
            if (request.GainAnalog is double ga) c.GainAnalog = ga;
            if (request.GainDigital is double gd) c.GainDigital = gd;
            if (request.GainMixed is double gm) c.GainMixed = gm;
        });
        if (updated == null) return NotFound();

        var snapshot = _manager.Current;
        var device = snapshot.Devices.FirstOrDefault(d => d.Config.Id == id);
        return Ok(ToDto(device ?? new SdrDevice(updated, null), snapshot.Plan));
    }

    /// <summary>Switches between Auto and Manual assignment.</summary>
    [HttpPut("mode")]
    [ProducesResponseType(typeof(RadiosResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetMode([FromBody] SetModeRequest request)
    {
        if (!Enum.TryParse<SdrAssignmentMode>(request.Mode, ignoreCase: true, out var mode))
            return BadRequest("Mode must be \"auto\" or \"manual\".");
        await _manager.SetModeAsync(mode);
        return Ok(BuildResponse(_manager.Current));
    }

    /// <summary>The current plan: which SDR carries each channel and how.</summary>
    [HttpGet("plan")]
    [ProducesResponseType(typeof(PlanDto), StatusCodes.Status200OK)]
    public PlanDto GetPlan() => ToDto(_manager.Current.Plan, _manager.Current.Devices);

    /// <summary>
    /// Plans with proposed pins without applying them, so the UI can show what an edit would
    /// do before it is saved.
    /// </summary>
    [HttpPost("plan/preview")]
    [ProducesResponseType(typeof(PlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Preview([FromBody] PreviewRequest request)
    {
        var mode = _manager.Current.Mode;
        if (request.Mode != null && !Enum.TryParse(request.Mode, ignoreCase: true, out mode))
            return BadRequest("Mode must be \"auto\" or \"manual\".");
        return Ok(ToDto(_manager.Preview(mode, request.Pins), _manager.Current.Devices));
    }

    /// <summary>
    /// Pins channels to an SDR for Manual mode, or unpins them with a null device. Pins are
    /// kept in Auto mode too, ready for switching back.
    /// </summary>
    [HttpPut("assignments")]
    [ProducesResponseType(typeof(RadiosResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Assign([FromBody] AssignRequest request)
    {
        if (request.ChannelIds == null || request.ChannelIds.Count == 0) return BadRequest("No channels given.");
        if (request.DeviceId != null && _manager.Current.Devices.All(d => d.Config.Id != request.DeviceId))
            return BadRequest("Unknown SDR.");

        await _db.SetChannelSdrDeviceAsync(request.ChannelIds, request.DeviceId);

        // Apply the pins to the loaded channels and re-plan now, so the response already
        // shows the result. A full reload happens in the background and will agree.
        var ids = request.ChannelIds.ToHashSet();
        foreach (var ch in _channels.Channels.Where(c => c.Id is int id && ids.Contains(id)))
            ch.SdrDeviceId = request.DeviceId;
        _manager.Replan();

        return Ok(BuildResponse(_manager.Current));
    }

    // ---------------------------------------------------------------- mapping

    private static RadiosResponse BuildResponse(SdrSnapshot snapshot)
    {
        var warnings = new List<string>();
        if (snapshot.Devices.Any(d => d.Attached is { SerialIsUnique: false }))
        {
            warnings.Add(
                "Some SDRs share a serial number, so they are told apart only by USB port order and their " +
                "settings may swap if they are replugged. Give each a unique serial with: rtl_eeprom -d <index> -s <serial>");
        }
        if (snapshot.Plan.Unscanned.Count > 0)
        {
            warnings.Add(
                $"{snapshot.Plan.Unscanned.Count} channel(s) are not scanned: every SDR is fast-scanning pinned " +
                "channels they don't fit beside. Pin them to an SDR, or switch to Auto.");
        }

        return new RadiosResponse(
            snapshot.Mode.ToString().ToLowerInvariant(),
            snapshot.Devices.Select(d => ToDto(d, snapshot.Plan)).ToList(),
            ToDto(snapshot.Plan, snapshot.Devices),
            warnings);
    }

    private static RadioDto ToDto(SdrDevice d, SdrPlan plan)
    {
        var a = plan.Devices.FirstOrDefault(p => p.Device.Id == d.Config.Id);
        return new RadioDto(
            d.Config.Id, d.Config.Label, d.Config.Serial, d.Config.Product,
            d.Attached != null, d.Attached?.Index, d.Attached?.SerialIsUnique ?? true,
            d.Config.Enabled, d.Config.Ppm, d.Config.GainAnalog, d.Config.GainDigital, d.Config.GainMixed,
            (a?.Role ?? SdrRole.Idle).ToString());
    }

    private static PlanDto ToDto(SdrPlan plan, IReadOnlyList<SdrDevice> devices)
    {
        var labels = devices.ToDictionary(d => d.Config.Id, d => d.Config.Label);
        return new PlanDto(
            TotalChannels: plan.ScannedCount + plan.Unscanned.Count,
            FastScanned: plan.FastScannedCount,
            Scanned: plan.ScannedCount,
            Devices: plan.Devices.Select(a =>
            {
                var freqs = a.Channels.Select(c => c.Frequency).ToList();
                return new PlanDeviceDto(
                    a.Device.Id,
                    labels.GetValueOrDefault(a.Device.Id, a.Device.Label),
                    a.Role.ToString(),
                    a.GainClass.ToString(),
                    a.GainFor(),
                    a.Role == SdrRole.FastScan ? a.Banks.FirstOrDefault()?.CenterFrequency : null,
                    freqs.Count > 1 ? Math.Round(freqs.Max() - freqs.Min(), 4) : 0,
                    freqs,
                    a.Channels.Select(c => c.Id).OfType<int>().ToList());
            }).ToList(),
            AutoPlacedChannelIds: plan.AutoPlaced.Select(c => c.Id).OfType<int>().ToList(),
            UnscannedChannelIds: plan.Unscanned.Select(c => c.Id).OfType<int>().ToList());
    }
}

/// <summary>Dongles, mode and plan.</summary>
public record RadiosResponse(string Mode, List<RadioDto> Devices, PlanDto Plan, List<string> Warnings);

/// <summary>One dongle and its settings.</summary>
public record RadioDto(
    string Id, string Label, string Serial, string Product,
    bool Attached, int? Index, bool SerialIsUnique,
    bool Enabled, double Ppm, double GainAnalog, double GainDigital, double GainMixed,
    string Role);

/// <summary>A plan: coverage figures and what each SDR carries.</summary>
public record PlanDto(
    int TotalChannels, int FastScanned, int Scanned,
    List<PlanDeviceDto> Devices,
    List<int> AutoPlacedChannelIds,
    List<int> UnscannedChannelIds);

/// <summary>What one SDR carries under a plan.</summary>
public record PlanDeviceDto(
    string DeviceId, string Label, string Role, string GainClass, double Gain,
    double? CenterFrequency, double SpanMHz, List<double> Frequencies, List<int> ChannelIds);

/// <summary>Edits to a dongle. Null fields are unchanged.</summary>
public record UpdateRadioRequest(
    string? Label = null, bool? Enabled = null, double? Ppm = null,
    double? GainAnalog = null, double? GainDigital = null, double? GainMixed = null);

/// <summary>"auto" or "manual".</summary>
public record SetModeRequest(string Mode);

/// <summary>A planning preview: optional mode and channel id → SDR id pins (null unpins).</summary>
public record PreviewRequest(string? Mode = null, Dictionary<int, string?>? Pins = null);

/// <summary>Pins channels to an SDR, or unpins them with a null device.</summary>
public record AssignRequest(List<int> ChannelIds, string? DeviceId);
