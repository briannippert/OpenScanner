using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;

namespace OpenScanner.Server.Controllers;

/// <summary>
/// AI call analysis: whether the remote WhisperServer supports it, the geolocated
/// incidents for the map, and the job that analyzes past transcripts.
/// </summary>
[ApiController]
[Route("api/ai")]
[Produces("application/json")]
public class AiController : ControllerBase
{
    private const int DefaultLimit = 2000;
    private const int MaxLimit = 5000;

    private readonly IDatabase _db;
    private readonly RemoteTranscriptionClient _remote;
    private readonly IncidentAnalysisService _analysis;

    public AiController(IDatabase db, RemoteTranscriptionClient remote, IncidentAnalysisService analysis)
    {
        _db = db;
        _remote = remote;
        _analysis = analysis;
    }

    /// <summary>Whether AI features can be used, and whether they are switched on.</summary>
    /// <param name="url">Server to check; defaults to the saved remote server URL. Lets the
    /// settings page check a URL before it is saved.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("capabilities")]
    [ProducesResponseType(typeof(AiCapabilities), StatusCodes.Status200OK)]
    public async Task<AiCapabilities> GetCapabilities([FromQuery] string? url, CancellationToken ct)
    {
        var target = string.IsNullOrWhiteSpace(url) ? await _remote.GetConfiguredUrlAsync() : url;
        var supported = target != null
            && Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && await _remote.HasTransmissionRouteAsync(target, ct);
        var enabled = string.Equals(await _db.GetSettingAsync(IncidentAnalysisService.EnabledSetting), "true", StringComparison.OrdinalIgnoreCase);
        return new AiCapabilities(supported, enabled);
    }

    /// <summary>Geolocated incidents, newest first.</summary>
    /// <param name="since">Start of the window (ISO 8601); defaults to 24 hours ago.</param>
    /// <param name="until">End of the window (ISO 8601); open-ended when omitted.</param>
    /// <param name="callTypes">Comma-separated call types to include; all when omitted.</param>
    /// <param name="service">police or fire; both when omitted.</param>
    /// <param name="limit">Maximum rows (1-5000, default 2000).</param>
    [HttpGet("calls")]
    [ProducesResponseType(typeof(IEnumerable<MapCall>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCalls([FromQuery] string? since, [FromQuery] string? until,
        [FromQuery] string? callTypes, [FromQuery] string? service, [FromQuery] int? limit)
    {
        DateTime sinceUtc = DateTime.UtcNow.AddHours(-24);
        if (!string.IsNullOrWhiteSpace(since) && !TryParseUtc(since, out sinceUtc))
        {
            return BadRequest(new { error = "since must be an ISO 8601 date/time." });
        }
        DateTime? untilUtc = null;
        if (!string.IsNullOrWhiteSpace(until))
        {
            if (!TryParseUtc(until, out var u)) return BadRequest(new { error = "until must be an ISO 8601 date/time." });
            untilUtc = u;
        }

        var types = string.IsNullOrWhiteSpace(callTypes)
            ? null
            : callTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var filter = new MapCallFilter(
            sinceUtc.ToString("o"),
            untilUtc?.ToString("o"),
            types,
            string.IsNullOrWhiteSpace(service) ? null : service.Trim().ToLowerInvariant(),
            Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit));

        return Ok(await _db.GetMapCallsAsync(filter));
    }

    /// <summary>Status of the job that analyzes past transcripts.</summary>
    [HttpGet("backfill")]
    [ProducesResponseType(typeof(BackfillStatus), StatusCodes.Status200OK)]
    public BackfillStatus GetBackfillStatus() => _analysis.GetBackfillStatus();

    /// <summary>Starts analyzing past transcripts. No-op if it is already running.</summary>
    [HttpPost("backfill/start")]
    [ProducesResponseType(typeof(BackfillStatus), StatusCodes.Status200OK)]
    public BackfillStatus StartBackfill()
    {
        _analysis.StartBackfill();
        return _analysis.GetBackfillStatus();
    }

    /// <summary>Stops the job after the current transmission.</summary>
    [HttpPost("backfill/stop")]
    [ProducesResponseType(typeof(BackfillStatus), StatusCodes.Status200OK)]
    public BackfillStatus StopBackfill()
    {
        _analysis.StopBackfill();
        return _analysis.GetBackfillStatus();
    }

    private static bool TryParseUtc(string value, out DateTime utc)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            utc = dto.UtcDateTime;
            return true;
        }
        utc = default;
        return false;
    }
}

/// <summary>Whether the remote server supports AI analysis and whether it is switched on.</summary>
public record AiCapabilities(bool Supported, bool Enabled);
