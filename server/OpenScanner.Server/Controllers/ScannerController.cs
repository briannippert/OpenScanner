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
    /// Sends a direct control command to the scanner service.
    /// </summary>
    /// <param name="body">JSON payload with 'action' (start, stop, scan, hold, set_squelch) and optional parameters.</param>
    [HttpPost("control")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ControlScanner([FromBody] JsonElement body)
    {
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
                    // Check if channel is avoided, if so, un-avoid it
                    var channels = await _db.GetAllChannelsAsync();
                    foreach (var ch in channels)
                    {
                        if (Math.Abs(ch.Frequency - targetFreq.Value) < 0.0001)
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
                    _radio.HoldFrequency(targetFreq.Value);
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
                if (body.TryGetProperty("frequency", out var df) && df.ValueKind == JsonValueKind.Number)
                    debugFreq = df.GetDouble();
                else if (body.TryGetProperty("frequency", out var dfs) && double.TryParse(dfs.GetString(), out var dfd))
                    debugFreq = dfd;

                if (debugFreq.HasValue)
                {
                    _radio.StartDebugSpectrum(debugFreq.Value);
                }
                break;
            default:
                return BadRequest($"Unknown action: {action}");
        }
        return Ok();
    }
}
