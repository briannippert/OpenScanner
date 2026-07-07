using Microsoft.AspNetCore.Mvc;
using OpenScanner.Server.Models;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Services;

namespace OpenScanner.Server.Controllers;

/// <summary>
/// Controller for managing Fire Tone Out settings.
/// </summary>
[ApiController]
[Route("api/firetones")]
[Produces("application/json")]
public class FiretonesController : ControllerBase
{
    private readonly IDatabase _db;
    private readonly ToneDetector _toneDetector;

    public FiretonesController(IDatabase db, ToneDetector toneDetector)
    {
        _db = db;
        _toneDetector = toneDetector;
    }

    /// <summary>
    /// Retrieves all configured Fire Tone Out sets.
    /// </summary>
    /// <returns>List of fire tone sets.</returns>
    [HttpGet("")]
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
    [HttpPost("")]
    [ProducesResponseType(typeof(FireToneSet), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddFireTone(FireToneSet tone)
    {
        var id = await _db.AddFireToneAsync(tone);
        tone.Id = id;
        _toneDetector.ReloadTones();
        return CreatedAtAction(nameof(GetFireTones), new { id }, tone);
    }

    /// <summary>
    /// Updates an existing Fire Tone Out set.
    /// </summary>
    /// <param name="id">ID of the tone set.</param>
    /// <param name="tone">Updated configuration.</param>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateFireTone(int id, FireToneSet tone)
    {
        tone.Id = id;
        await _db.UpdateFireToneAsync(tone);
        _toneDetector.ReloadTones();
        return Ok();
    }

    /// <summary>
    /// Deletes a Fire Tone Out set.
    /// </summary>
    /// <param name="id">ID of the tone set.</param>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteFireTone(int id)
    {
        await _db.DeleteFireToneAsync(id);
        _toneDetector.ReloadTones();
        return Ok();
    }
}
