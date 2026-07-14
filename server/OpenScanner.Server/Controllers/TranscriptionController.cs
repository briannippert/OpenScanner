using Microsoft.AspNetCore.Mvc;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Services;

namespace OpenScanner.Server.Controllers;

/// <summary>
/// Controls the on-demand transcription backfill job and proxies queries to a
/// remote OpenScanner.WhisperServer (models list / health) for the settings UI.
/// </summary>
[ApiController]
[Route("api/transcription")]
[Produces("application/json")]
public class TranscriptionController : ControllerBase
{
    private readonly IBackfillService _backfill;
    private readonly RemoteTranscriptionClient _remoteClient;

    public TranscriptionController(IBackfillService backfill, RemoteTranscriptionClient remoteClient)
    {
        _backfill = backfill;
        _remoteClient = remoteClient;
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

    /// <summary>
    /// Lists the models installed on a remote WhisperServer, used to populate the
    /// remote-model dropdown. Proxied server-side so the browser stays same-origin.
    /// </summary>
    [HttpGet("remote/models")]
    [ProducesResponseType(typeof(IReadOnlyList<RemoteModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRemoteModels([FromQuery] string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return BadRequest(new { error = "A valid absolute server URL is required." });
        }
        try
        {
            var models = await _remoteClient.GetModelsAsync(url, ct);
            return Ok(models);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"Could not reach server: {ex.Message}" });
        }
    }

    /// <summary>
    /// Probes a remote WhisperServer's /health endpoint for the settings "Test" button.
    /// </summary>
    [HttpGet("remote/health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRemoteHealth([FromQuery] string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return BadRequest(new { error = "A valid absolute server URL is required." });
        }
        var health = await _remoteClient.HealthAsync(url, ct);
        if (health == null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { ok = false, error = "Could not reach server." });
        }
        return Ok(new { ok = true, health = health.Value });
    }
}
