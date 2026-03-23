using Microsoft.AspNetCore.Mvc;
using OpenScanner.Server.Models;
using OpenScanner.Server.Interfaces;

namespace OpenScanner.Server.Controllers;

/// <summary>
/// Controller for accessing transmission history and logs.
/// </summary>
[ApiController]
[Route("api/history")]
[Produces("application/json")]
public class HistoryController : ControllerBase
{
    private readonly IDatabase _db;

    public HistoryController(IDatabase db)
    {
        _db = db;
    }

    /// <summary>
    /// Retrieves the most recent transmission logs.
    /// </summary>
    /// <returns>A list of the latest 100 transmission logs.</returns>
    [HttpGet("")]
    [ProducesResponseType(typeof(IEnumerable<CallLog>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<CallLog>> GetHistory()
    {
        return await _db.GetHistoryAsync(100);
    }

    /// <summary>
    /// Gets all years for which transmission data exists.
    /// </summary>
    /// <returns>List of years (e.g., ["2023", "2024"]).</returns>
    [HttpGet("years")]
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
    [HttpGet("{year}/months")]
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
    [HttpGet("{year}/{month}/days")]
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
    [HttpGet("{year}/{month}/{day}/channels")]
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
    [HttpGet("filter")]
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
    [HttpGet("search")]
    [ProducesResponseType(typeof(IEnumerable<CallLog>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<CallLog>> SearchTransmissions(string q)
    {
        return await _db.SearchTransmissionsAsync(q);
    }

    /// <summary>
    /// Retrieves all favorited transmission logs.
    /// </summary>
    /// <returns>List of favorited call logs.</returns>
    [HttpGet("favorites")]
    [ProducesResponseType(typeof(IEnumerable<CallLog>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<CallLog>> GetFavorites()
    {
        return await _db.GetFavoritesAsync();
    }

    /// <summary>
    /// Sets or clears the favorite flag on a transmission.
    /// </summary>
    /// <param name="id">Transmission ID.</param>
    /// <param name="request">Body containing the new favorite state.</param>
    [HttpPut("{id}/favorite")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetFavorite(string id, [FromBody] SetFavoriteRequest request)
    {
        await _db.SetFavoriteAsync(id, request.IsFavorite);
        return Ok();
    }

    /// <summary>
    /// Deletes a specific transmission log and its audio file.
    /// </summary>
    /// <param name="id">Transmission ID.</param>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteTransmission(string id)
    {
        await _db.DeleteTransmissionAsync(id);
        return Ok();
    }

    /// <summary>
    /// Clears the entire transmission history and deletes all audio files.
    /// </summary>
    [HttpDelete("")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClearHistory()
    {
        await _db.ClearHistoryAsync();
        return Ok();
    }
}

/// <summary>
/// Request body for setting a transmission's favorite status.
/// </summary>
public record SetFavoriteRequest(bool IsFavorite);
