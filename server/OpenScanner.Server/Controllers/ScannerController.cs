using Microsoft.AspNetCore.Mvc;
using OpenScanner.Server.Models;
using OpenScanner.Server.Interfaces;
using System.Text.Json;

namespace OpenScanner.Server.Controllers;

/// <summary>
/// Controller for managing scanner channels and real-time control.
/// </summary>
[ApiController]
[Route("api")]
[Produces("application/json")]
public class ScannerController : ControllerBase
{
    private readonly IDatabase _db;
    private readonly IRadioSource _radio;

    public ScannerController(IDatabase db, IRadioSource radio)
    {
        _db = db;
        _radio = radio;
    }

    /// <summary>
    /// Retrieves all configured radio channels.
    /// </summary>
    /// <returns>A list of channels.</returns>
    [HttpGet("channels")]
    [ProducesResponseType(typeof(IEnumerable<Channel>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<Channel>> GetAllChannels()
    {
        return await _db.GetAllChannelsAsync();
    }

    /// <summary>
    /// Adds a new radio channel.
    /// </summary>
    /// <param name="channel">The channel details.</param>
    /// <returns>The created channel with its ID.</returns>
    [HttpPost("channels")]
    [ProducesResponseType(typeof(Channel), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddChannel(Channel channel)
    {
        var id = await _db.AddChannelAsync(channel);
        channel.Id = id;
        _radio.ReloadChannels();
        return CreatedAtAction(nameof(GetAllChannels), new { id }, channel);
    }

    /// <summary>
    /// Updates an existing channel.
    /// </summary>
    /// <param name="id">The ID of the channel to update.</param>
    /// <param name="channel">The updated channel details.</param>
    [HttpPut("channels/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateChannel(int id, Channel channel)
    {
        channel.Id = id;
        
        // If we are avoiding this channel, check if we are currently holding it
        if (channel.Avoid)
        {
            var state = _radio.GetState();
            if (state.ManualHoldFrequency.HasValue && Math.Abs(state.ManualHoldFrequency.Value - channel.Frequency) < 0.0001)
            {
                _radio.ResumeScan();
            }
        }

        await _db.UpdateChannelAsync(channel);
        _radio.ReloadChannels();
        return Ok();
    }

    /// <summary>
    /// Deletes a channel.
    /// </summary>
    /// <param name="id">The ID of the channel to delete.</param>
    [HttpDelete("channels/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteChannel(int id)
    {
        await _db.DeleteChannelAsync(id);
        _radio.ReloadChannels();
        return Ok();
    }

    /// <summary>
    /// Retrieves the current real-time state of the scanner.
    /// </summary>
    /// <returns>The current <see cref="ScannerState"/>.</returns>
    [HttpGet("scanner")]
    [ProducesResponseType(typeof(ScannerState), StatusCodes.Status200OK)]
    public ScannerState GetStatus() => _radio.GetState();

    /// <summary>
    /// Turns the scanner on (start) or off (stop).
    /// </summary>
    /// <param name="request">Whether the scanner should be enabled.</param>
    [HttpPut("scanner/power")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult SetPower([FromBody] PowerRequest request)
    {
        if (request.Enabled) _radio.Start();
        else _radio.Stop();
        return Ok();
    }

    /// <summary>
    /// Holds the scanner on a specific frequency, un-avoiding it if necessary.
    /// </summary>
    /// <param name="request">The frequency to hold, in MHz.</param>
    [HttpPut("scanner/hold")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Hold([FromBody] HoldRequest request)
    {
        await HoldFrequencyInternal(request.Frequency);
        return Ok();
    }

    /// <summary>
    /// Releases any active hold and resumes normal scanning.
    /// </summary>
    [HttpDelete("scanner/hold")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult ReleaseHold()
    {
        _radio.ResumeScan();
        return Ok();
    }

    /// <summary>
    /// Sets the squelch threshold.
    /// </summary>
    /// <param name="request">The squelch threshold, in dB.</param>
    [HttpPut("scanner/squelch")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult SetSquelch([FromBody] SquelchRequest request)
    {
        _radio.SetSquelch(request.Value);
        return Ok();
    }

    /// <summary>
    /// Temporarily avoids a frequency for a given duration.
    /// </summary>
    /// <param name="request">The frequency to avoid and how long to avoid it.</param>
    [HttpPost("scanner/avoids")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult AddAvoid([FromBody] AvoidRequest request)
    {
        _radio.AvoidFrequency(request.Frequency, request.Duration);
        return Accepted();
    }

    /// <summary>
    /// Starts recording raw IQ data to a file.
    /// </summary>
    /// <param name="request">An optional label for the dump filename.</param>
    [HttpPost("scanner/iq-dump")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult StartIqDump([FromBody] IqDumpRequest? request)
    {
        _radio.StartDumping(request?.Label ?? "sample");
        return Ok();
    }

    /// <summary>
    /// Stops recording raw IQ data.
    /// </summary>
    [HttpDelete("scanner/iq-dump")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult StopIqDump()
    {
        _radio.StopDumping();
        return Ok();
    }

    /// <summary>
    /// Starts high-bandwidth RF spectrum debug mode.
    /// </summary>
    /// <param name="request">The center frequency (MHz) and optional hardware gain (dB).</param>
    [HttpPost("scanner/debug-spectrum")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult StartDebugSpectrum([FromBody] DebugSpectrumRequest request)
    {
        _radio.StartDebugSpectrum(request.Frequency, request.Gain);
        return Ok();
    }

    /// <summary>
    /// Holds the scanner on a frequency, un-avoiding a matching channel first.
    /// </summary>
    private async Task HoldFrequencyInternal(double frequency)
    {
        var channels = await _db.GetAllChannelsAsync();
        foreach (var ch in channels)
        {
            if (Math.Abs(ch.Frequency - frequency) < 0.0001)
            {
                if (ch.Avoid)
                {
                    ch.Avoid = false;
                    await _db.UpdateChannelAsync(ch);
                    _radio.ReloadChannels();
                }
                break;
            }
        }
        _radio.HoldFrequency(frequency);
    }

    /// <summary>
    /// Sends a direct control command to the scanner service.
    /// </summary>
    /// <remarks>
    /// Deprecated: use the resource-oriented <c>/api/scanner/*</c> endpoints instead.
    /// This action-dispatch endpoint is retained for backward compatibility.
    /// </remarks>
    /// <param name="body">JSON payload with 'action' (start, stop, scan, hold, set_squelch) and optional parameters.</param>
    [HttpPost("control")]
    [Obsolete("Use the resource-oriented /api/scanner/* endpoints instead.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ControlScanner([FromBody] JsonElement body)
    {
        Response.Headers.Append("Deprecation", "true");
        Response.Headers.Append("Link", "</api/scanner>; rel=\"successor-version\"");
        if (!body.TryGetProperty("action", out var actionProp))
            return BadRequest("Action is required");

        var action = actionProp.GetString();
        
        switch (action)
        {
            case "start": _radio.Start(); break;
            case "stop": _radio.Stop(); break;
            case "scan": _radio.ResumeScan(); break;
            case "avoid":
                double? avoidFreq = null;
                double avoidDuration = 10; // Default 10 seconds

                if (body.TryGetProperty("frequency", out var af) && af.ValueKind == JsonValueKind.Number)
                    avoidFreq = af.GetDouble();
                else if (body.TryGetProperty("frequency", out var afs) && double.TryParse(afs.GetString(), out var afd))
                    avoidFreq = afd;

                if (body.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
                    avoidDuration = dur.GetDouble();

                if (avoidFreq.HasValue)
                {
                    _radio.AvoidFrequency(avoidFreq.Value, avoidDuration);
                }
                break;
            case "hold": 
                double? targetFreq = null;
                if (body.TryGetProperty("frequency", out var f) && f.ValueKind == JsonValueKind.Number)
                    targetFreq = f.GetDouble();
                else if (body.TryGetProperty("frequency", out var fs) && double.TryParse(fs.GetString(), out var fd))
                    targetFreq = fd;
                
                if (targetFreq.HasValue)
                {
                    await HoldFrequencyInternal(targetFreq.Value);
                }
                break;
            case "set_squelch":
                 if (body.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number)
                    _radio.SetSquelch(v.GetDouble());
                 else if (body.TryGetProperty("value", out var vs) && double.TryParse(vs.GetString(), out var vd))
                    _radio.SetSquelch(vd);
                break;
            case "start_dump":
                if (body.TryGetProperty("label", out var labelProp))
                    _radio.StartDumping(labelProp.GetString() ?? "sample");
                else
                    _radio.StartDumping("sample");
                break;
            case "stop_dump":
                _radio.StopDumping();
                break;
            case "debug_spectrum":
                double? debugFreq = null;
                double? debugGain = null;

                if (body.TryGetProperty("frequency", out var df) && df.ValueKind == JsonValueKind.Number)
                    debugFreq = df.GetDouble();
                else if (body.TryGetProperty("frequency", out var dfs) && double.TryParse(dfs.GetString(), out var dfd))
                    debugFreq = dfd;

                if (body.TryGetProperty("value", out var gv) && gv.ValueKind == JsonValueKind.Number)
                    debugGain = gv.GetDouble();
                else if (body.TryGetProperty("gain", out var gs) && double.TryParse(gs.GetString(), out var gd))
                    debugGain = gd;

                if (debugFreq.HasValue)
                {
                    _radio.StartDebugSpectrum(debugFreq.Value, debugGain);
                }
                break;
            default:
                return BadRequest($"Unknown action: {action}");
        }
        return Ok();
    }
}

/// <summary>Request to turn the scanner on or off.</summary>
public record PowerRequest(bool Enabled);

/// <summary>Request to hold the scanner on a frequency (MHz).</summary>
public record HoldRequest(double Frequency);

/// <summary>Request to set the squelch threshold (dB).</summary>
public record SquelchRequest(double Value);

/// <summary>Request to temporarily avoid a frequency (MHz) for a duration (seconds).</summary>
public record AvoidRequest(double Frequency, double Duration = 10);

/// <summary>Request to start an IQ dump with an optional filename label.</summary>
public record IqDumpRequest(string? Label = null);

/// <summary>Request to start RF spectrum debug mode at a center frequency (MHz) with optional gain (dB).</summary>
public record DebugSpectrumRequest(double Frequency, double? Gain = null);
