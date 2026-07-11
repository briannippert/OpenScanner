using Microsoft.AspNetCore.Mvc;
using OpenScanner.Server.Interfaces;

namespace OpenScanner.Server.Controllers;

/// <summary>
/// Controls the on-demand transcription backfill job.
/// </summary>
[ApiController]
[Route("api/transcription")]
[Produces("application/json")]
public class TranscriptionController : ControllerBase
{
    private readonly IBackfillService _backfill;

    public TranscriptionController(IBackfillService backfill)
    {
        _backfill = backfill;
    }

    /// <summary>
    /// Gets the current status/progress of the transcription backfill job.
    /// </summary>
    [HttpGet("backfill")]
    [ProducesResponseType(typeof(OpenScanner.Server.Models.BackfillStatus), StatusCodes.Status200OK)]
    public IActionResult GetBackfillStatus()
    {
        return Ok(_backfill.GetStatus());
    }

    /// <summary>
    /// Starts the backfill job. No-op if it is already running.
    /// </summary>
    [HttpPost("backfill/start")]
    [ProducesResponseType(typeof(OpenScanner.Server.Models.BackfillStatus), StatusCodes.Status200OK)]
    public IActionResult StartBackfill()
    {
        _backfill.Start();
        return Ok(_backfill.GetStatus());
    }

    /// <summary>
    /// Requests the running backfill job to stop after the current clip.
    /// </summary>
    [HttpPost("backfill/stop")]
    [ProducesResponseType(typeof(OpenScanner.Server.Models.BackfillStatus), StatusCodes.Status200OK)]
    public IActionResult StopBackfill()
    {
        _backfill.Stop();
        return Ok(_backfill.GetStatus());
    }
}
