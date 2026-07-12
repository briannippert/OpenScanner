using Microsoft.AspNetCore.Mvc;
using OpenScanner.Server.Models;
using OpenScanner.Server.Interfaces;

namespace OpenScanner.Server.Controllers;

/// <summary>
/// Controller for per-channel radio aliases: display names for source IDs (SRC)
/// and talkgroups (TG), plus discovery of recently-seen candidates.
/// </summary>
[ApiController]
[Route("api/aliases")]
[Produces("application/json")]
public class AliasesController : ControllerBase
{
    private readonly IDatabase _db;

    public AliasesController(IDatabase db)
    {
        _db = db;
    }

    /// <summary>Retrieves all configured radio aliases.</summary>
    [HttpGet("")]
    [ProducesResponseType(typeof(IEnumerable<RadioAlias>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<RadioAlias>> GetAliases()
    {
        return await _db.GetAliasesAsync();
    }

    /// <summary>
    /// Lists the distinct SRC and TG values seen on each channel within the last
    /// <paramref name="days"/> days (default 7), with counts and last-seen times.
    /// </summary>
    [HttpGet("candidates")]
    [ProducesResponseType(typeof(IEnumerable<AliasCandidate>), StatusCodes.Status200OK)]
    public async Task<IEnumerable<AliasCandidate>> GetCandidates([FromQuery] int days = 7)
    {
        return await _db.GetAliasCandidatesAsync(days);
    }

    /// <summary>Adds (or updates on conflict) a radio alias.</summary>
    [HttpPost("")]
    [ProducesResponseType(typeof(RadioAlias), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddAlias(RadioAlias alias)
    {
        alias.Id = await _db.AddAliasAsync(alias);
        return CreatedAtAction(nameof(GetAliases), new { id = alias.Id }, alias);
    }

    /// <summary>Updates an existing radio alias.</summary>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateAlias(int id, RadioAlias alias)
    {
        alias.Id = id;
        await _db.UpdateAliasAsync(alias);
        return Ok();
    }

    /// <summary>Deletes a radio alias.</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteAlias(int id)
    {
        await _db.DeleteAliasAsync(id);
        return Ok();
    }

    /// <summary>
    /// Imports aliases additively — fills in blanks without overwriting existing names.
    /// Returns the count added.
    /// </summary>
    [HttpPost("import")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Import(IEnumerable<RadioAlias> aliases)
    {
        var added = await _db.ImportAliasesAsync(aliases);
        return Ok(new { added });
    }
}
