using Microsoft.AspNetCore.Mvc;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using System.Text.Json;

namespace OpenScanner.Server.Controllers;

[ApiController]
[Route("api")]
public class OpenScannerController : ControllerBase
{
    private readonly IDatabase _db;
    private readonly RtlDevice _radio;

    public OpenScannerController(IDatabase db, RtlDevice radio)
    {
        _db = db;
        _radio = radio;
    }

    [HttpGet("channels")]
    [ProducesResponseType(typeof(IEnumerable<Channel>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<Channel>> GetAllChannels()
    {
        return await _db.GetAllChannelsAsync();
    }

    [HttpPost("channels")]
    [ProducesResponseType(typeof(Channel), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddChannel(Channel channel)
    {
        var id = await _db.AddChannelAsync(channel);
        channel.Id = id;
        _radio.ReloadChannels();
        return CreatedAtAction(nameof(GetAllChannels), new { id }, channel);
    }

    [HttpPut("channels/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateChannel(int id, Channel channel)
    {
        channel.Id = id;
        await _db.UpdateChannelAsync(channel);
        _radio.ReloadChannels();
        return Ok();
    }

    [HttpDelete("channels/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteChannel(int id)
    {
        await _db.DeleteChannelAsync(id);
        _radio.ReloadChannels();
        return Ok();
    }

    [HttpGet("history")]
    [ProducesResponseType(typeof(IEnumerable<CallLog>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<CallLog>> GetHistory()
    {
        return await _db.GetHistoryAsync(100);
    }

    [HttpGet("history/years")]
    [ProducesResponseType(typeof(IEnumerable<string>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<string>> GetYears()
    {
        return await _db.GetTransmissionYearsAsync();
    }

    [HttpGet("history/{year}/months")]
    [ProducesResponseType(typeof(IEnumerable<string>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<string>> GetMonths(string year)
    {
        return await _db.GetTransmissionMonthsAsync(year);
    }

    [HttpGet("history/{year}/{month}/days")]
    [ProducesResponseType(typeof(IEnumerable<string>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<string>> GetDays(string year, string month)
    {
        return await _db.GetTransmissionDaysAsync(year, month);
    }

    [HttpGet("history/{year}/{month}/{day}/channels")]
    [ProducesResponseType(typeof(IEnumerable<dynamic>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<dynamic>> GetChannelsForDay(string year, string month, string day)
    {
        return await _db.GetTransmissionChannelsAsync(year, month, day);
    }

    [HttpGet("history/filter")]
    [ProducesResponseType(typeof(IEnumerable<CallLog>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<CallLog>> GetFilteredTransmissions(string year, string month, string day, string alphaTag, double frequency)
    {
        return await _db.GetTransmissionsAsync(year, month, day, alphaTag, frequency);
    }

    [HttpGet("history/search")]
    [ProducesResponseType(typeof(IEnumerable<CallLog>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<CallLog>> SearchTransmissions(string q)
    {
        return await _db.SearchTransmissionsAsync(q);
    }

    [HttpDelete("history/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteTransmission(string id)
    {
        await _db.DeleteTransmissionAsync(id);
        return Ok();
    }

    [HttpDelete("history")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClearHistory()
    {
        await _db.ClearHistoryAsync();
        return Ok();
    }

    [HttpGet("firetones")]
    public async Task<IEnumerable<FireToneSet>> GetFireTones()
    {
        return await _db.GetAllFireTonesAsync();
    }

    [HttpPost("firetones")]
    public async Task<IActionResult> AddFireTone(FireToneSet tone)
    {
        var id = await _db.AddFireToneAsync(tone);
        tone.Id = id;
        return CreatedAtAction(nameof(GetFireTones), new { id }, tone);
    }

    [HttpPut("firetones/{id}")]
    public async Task<IActionResult> UpdateFireTone(int id, FireToneSet tone)
    {
        tone.Id = id;
        await _db.UpdateFireToneAsync(tone);
        return Ok();
    }

    [HttpDelete("firetones/{id}")]
    public async Task<IActionResult> DeleteFireTone(int id)
    {
        await _db.DeleteFireToneAsync(id);
        return Ok();
    }

    [HttpGet("settings")]
    public async Task<Dictionary<string, string>> GetSettings()
    {
        return await _db.GetAllSettingsAsync();
    }

    [HttpPost("settings/{key}")]
    public async Task<IActionResult> UpdateSetting(string key, [FromBody] string value)
    {
        await _db.SetSettingAsync(key, value);
        return Ok();
    }

    [HttpPost("control")]
    [ProducesResponseType(StatusCodes.Status200OK)]
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
            default:
                return BadRequest($"Unknown action: {action}");
        }
        return Ok();
    }
}
