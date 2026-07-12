using Microsoft.AspNetCore.Mvc;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Controllers;

/// <summary>
/// Controller for the in-UI self-updater: reports update availability and applies updates.
/// </summary>
[ApiController]
[Route("api/update")]
[Produces("application/json")]
public class UpdateController : ControllerBase
{
    private readonly IUpdateService _update;

    public UpdateController(IUpdateService update)
    {
        _update = update;
    }

    /// <summary>Returns the current updater snapshot (state, versions, availability, and log).</summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(UpdateStatus), StatusCodes.Status200OK)]
    public IActionResult GetStatus() => Ok(_update.GetStatus());

    /// <summary>Forces a check against the latest GitHub release and returns the refreshed snapshot.</summary>
    [HttpPost("check")]
    [ProducesResponseType(typeof(UpdateStatus), StatusCodes.Status200OK)]
    public async Task<IActionResult> Check() => Ok(await _update.CheckAsync(force: true));

    /// <summary>
    /// Starts an update in the background. Returns 202 Accepted, or 409 Conflict if an
    /// update is already running.
    /// </summary>
    [HttpPost("start")]
    [ProducesResponseType(typeof(UpdateStatus), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult Start()
    {
        if (!_update.TryStartUpdate())
            return Conflict(new { message = "An update is already in progress." });
        return Accepted(_update.GetStatus());
    }
}
