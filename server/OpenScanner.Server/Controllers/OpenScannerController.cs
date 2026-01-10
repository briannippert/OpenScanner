using Microsoft.AspNetCore.Mvc;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using OpenScanner.Server.Interfaces;
using System.Text.Json;

namespace OpenScanner.Server.Controllers;

/// <summary>
/// Main controller for OpenScanner operations.
/// </summary>
[ApiController]
[Route("api")]
[Produces("application/json")]
public class OpenScannerController : ControllerBase
{
    private readonly IDatabase _db;
    private readonly IRadioSource _radio;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenScannerController"/> class.
    /// </summary>
    /// <param name="db">The database interface.</param>
    /// <param name="radio">The radio source interface.</param>
    public OpenScannerController(IDatabase db, IRadioSource radio)
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
    /// Retrieves the most recent transmission logs.
    /// </summary>
    /// <returns>A list of the latest 100 transmission logs.</returns>
    [HttpGet("history")]
    [ProducesResponseType(typeof(IEnumerable<CallLog>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<CallLog>> GetHistory()
    {
        return await _db.GetHistoryAsync(100);
    }

    /// <summary>
    /// Gets all years for which transmission data exists.
    /// </summary>
    /// <returns>List of years (e.g., ["2023", "2024"]).</returns>
    [HttpGet("history/years")]
    [ProducesResponseType(typeof(IEnumerable<string>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<string>> GetYears()
    {
        return await _db.GetTransmissionYearsAsync();
    }

    /// <summary>
    /// Gets months with data for a specific year.
    /// </summary>
    /// <param name="year">The year (e.g., "2024").</param>
    /// <returns>List of months.</returns>
    [HttpGet("history/{year}/months")]
    [ProducesResponseType(typeof(IEnumerable<string>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<string>> GetMonths(string year)
    {
        return await _db.GetTransmissionMonthsAsync(year);
    }

    /// <summary>
    /// Gets days with data for a specific month and year.
    /// </summary>
    /// <param name="year">The year.</param>
    /// <param name="month">The month.</param>
    /// <returns>List of days.</returns>
    [HttpGet("history/{year}/{month}/days")]
    [ProducesResponseType(typeof(IEnumerable<string>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<string>> GetDays(string year, string month)
    {
        return await _db.GetTransmissionDaysAsync(year, month);
    }

    /// <summary>
    /// Gets channels active on a specific day.
    /// </summary>
    /// <param name="year">The year.</param>
    /// <param name="month">The month.</param>
    /// <param name="day">The day.</param>
    /// <returns>List of channels with activity counts.</returns>
    [HttpGet("history/{year}/{month}/{day}/channels")]
    [ProducesResponseType(typeof(IEnumerable<dynamic>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<dynamic>> GetChannelsForDay(string year, string month, string day)
    {
        return await _db.GetTransmissionChannelsAsync(year, month, day);
    }

    /// <summary>
    /// Filters transmissions by date and channel.
    /// </summary>
    /// <param name="year">Year.</param>
    /// <param name="month">Month.</param>
    /// <param name="day">Day.</param>
    /// <param name="alphaTag">Channel Name.</param>
    /// <param name="frequency">Frequency in MHz.</param>
    /// <returns>Filtered logs.</returns>
    [HttpGet("history/filter")]
    [ProducesResponseType(typeof(IEnumerable<CallLog>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<CallLog>> GetFilteredTransmissions(string year, string month, string day, string alphaTag, double frequency)
    {
        return await _db.GetTransmissionsAsync(year, month, day, alphaTag, frequency);
    }

    /// <summary>
    /// Searches transmissions by text content (transcription or metadata).
    /// </summary>
    /// <param name="q">Search query string.</param>
    /// <returns>Matching logs.</returns>
    [HttpGet("history/search")]
    [ProducesResponseType(typeof(IEnumerable<CallLog>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<CallLog>> SearchTransmissions(string q)
    {
        return await _db.SearchTransmissionsAsync(q);
    }

    /// <summary>
    /// Deletes a specific transmission log and its audio file.
    /// </summary>
    /// <param name="id">Transmission ID.</param>
    [HttpDelete("history/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteTransmission(string id)
    {
        await _db.DeleteTransmissionAsync(id);
        return Ok();
    }

    /// <summary>
    /// Clears the entire transmission history and deletes all audio files.
    /// </summary>
    [HttpDelete("history")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClearHistory()
    {
        await _db.ClearHistoryAsync();
        return Ok();
    }

    /// <summary>
    /// Retrieves all configured Fire Tone Out sets.
    /// </summary>
    /// <returns>List of fire tone sets.</returns>
    [HttpGet("firetones")]
    [ProducesResponseType(typeof(IEnumerable<FireToneSet>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<FireToneSet>> GetFireTones()
    {
        return await _db.GetAllFireTonesAsync();
    }

    /// <summary>
    /// Adds a new Fire Tone Out set.
    /// </summary>
    /// <param name="tone">The tone set configuration.</param>
    /// <returns>The created tone set.</returns>
    [HttpPost("firetones")]
    [ProducesResponseType(typeof(FireToneSet), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddFireTone(FireToneSet tone)
    {
        var id = await _db.AddFireToneAsync(tone);
        tone.Id = id;
        return CreatedAtAction(nameof(GetFireTones), new { id }, tone);
    }

    /// <summary>
    /// Updates an existing Fire Tone Out set.
    /// </summary>
    /// <param name="id">ID of the tone set.</param>
    /// <param name="tone">Updated configuration.</param>
    [HttpPut("firetones/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateFireTone(int id, FireToneSet tone)
    {
        tone.Id = id;
        await _db.UpdateFireToneAsync(tone);
        return Ok();
    }

    /// <summary>
    /// Deletes a Fire Tone Out set.
    /// </summary>
    /// <param name="id">ID of the tone set.</param>
    [HttpDelete("firetones/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteFireTone(int id)
    {
        await _db.DeleteFireToneAsync(id);
        return Ok();
    }

    /// <summary>
    /// Retrieves all system settings.
    /// </summary>
    /// <returns>Dictionary of key-value settings.</returns>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(Dictionary<string, string>), StatusCodes.Status200OK)]
    public async Task<Dictionary<string, string>> GetSettings()
    {
        return await _db.GetAllSettingsAsync();
    }

    /// <summary>
    /// Updates a specific system setting.
    /// </summary>
    /// <param name="key">The setting key (e.g., "EnableTranscription").</param>
    /// <param name="value">The new value.</param>
    [HttpPost("settings/{key}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateSetting(string key, [FromBody] string value)
    {
        await _db.SetSettingAsync(key, value);
        return Ok();
    }

    /// <summary>
    /// Sends a direct control command to the scanner service.
    /// </summary>
    /// <param name="body">JSON payload with 'action' (start, stop, scan, hold, set_squelch) and optional parameters.</param>
    [HttpPost("control")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult ControlScanner([FromBody] JsonElement body)
    {
        if (!body.TryGetProperty("action", out var actionProp))
            return BadRequest("Action is required");

        var action = actionProp.GetString();
        
        switch (action)
        {
            case "start": _radio.Start(); break;
            case "stop": _radio.Stop(); break;
            case "scan": _radio.ResumeScan(); break;
            case "hold": 
                if (body.TryGetProperty("frequency", out var f) && f.ValueKind == JsonValueKind.Number)
                    _radio.HoldFrequency(f.GetDouble());
                else if (body.TryGetProperty("frequency", out var fs) && double.TryParse(fs.GetString(), out var fd))
                     _radio.HoldFrequency(fd);
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
            default:
                return BadRequest($"Unknown action: {action}");
        }
        return Ok();
    }
}
