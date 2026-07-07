using Microsoft.AspNetCore.Mvc;
using OpenScanner.Server.Models;
using OpenScanner.Server.Interfaces;

namespace OpenScanner.Server.Controllers;

/// <summary>
/// Controller for accessing the radio event log — fire tone-out and MDC1200 detections.
/// </summary>
[ApiController]
[Route("api/events")]
[Produces("application/json")]
public class EventsController : ControllerBase
{
    private readonly IDatabase _db;

    public EventsController(IDatabase db)
    {
        _db = db;
    }

    /// <summary>
    /// Retrieves the most recent radio events (newest first).
    /// </summary>
    /// <returns>A list of the latest 100 radio events.</returns>
    [HttpGet("")]
    [ProducesResponseType(typeof(IEnumerable<RadioEvent>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<RadioEvent>> GetEvents()
    {
        return await _db.GetRadioEventsAsync(100);
    }

    /// <summary>
    /// Clears all stored radio events.
    /// </summary>
    [HttpDelete("")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClearEvents()
    {
        await _db.ClearRadioEventsAsync();
        return Ok();
    }
}
