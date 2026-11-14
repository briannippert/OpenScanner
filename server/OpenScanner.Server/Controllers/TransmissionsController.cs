using Microsoft.AspNetCore.Mvc;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Controllers;

/// <summary>
/// Paged, filterable access to recorded transmissions. Supersedes the fixed-size
/// lists under <c>/api/history</c> (recent, search, favorites, filter).
/// </summary>
[ApiController]
[Route("api/transmissions")]
[Produces("application/json")]
public class TransmissionsController : ControllerBase
{
    /// <summary>Page size when <c>limit</c> is omitted.</summary>
    public const int DefaultLimit = 50;
    /// <summary>Largest page size, and the most ids one batch request may name.</summary>
    public const int MaxLimit = 200;

    private readonly IDatabase _db;

    public TransmissionsController(IDatabase db)
    {
        _db = db;
    }

    /// <summary>
    /// Transmissions newest first, one page at a time. Pass the response's
    /// <c>nextCursor</c> back as <c>cursor</c> to get the next older page; it is null on
    /// the last page. All filters are optional and combine with AND.
    /// </summary>
    /// <param name="limit">Page size (1-200, default 50).</param>
    /// <param name="cursor">nextCursor from the previous page.</param>
    /// <param name="since">Only transmissions at or after this time (ISO 8601; UTC if no offset).</param>
    /// <param name="until">Only transmissions before this time (ISO 8601; UTC if no offset).</param>
    /// <param name="channel">Exact channel alpha tag.</param>
    /// <param name="frequency">Frequency in MHz.</param>
    /// <param name="q">Text to find in the transcription, description, alpha tag or frequency.</param>
    /// <param name="favorites">Only starred transmissions.</param>
    /// <param name="tones">Only fire tone-outs.</param>
    /// <param name="incidents">Only transmissions the AI analysis classed as incidents.</param>
    /// <param name="ids">Comma-separated ids to fetch in one request (at most 200). The
    /// other parameters are ignored; ids that no longer exist are left out.</param>
    [HttpGet("")]
    [ProducesResponseType(typeof(TransmissionPage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Get(
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        [FromQuery] string? since,
        [FromQuery] string? until,
        [FromQuery] string? channel,
        [FromQuery] double? frequency,
        [FromQuery] string? q,
        [FromQuery] bool favorites = false,
        [FromQuery] bool tones = false,
        [FromQuery] bool incidents = false,
        [FromQuery] string? ids = null)
    {
        if (!string.IsNullOrWhiteSpace(ids))
        {
            var idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct()
                .ToList();
            if (idList.Count > MaxLimit)
            {
                return BadRequest(new { error = $"ids may name at most {MaxLimit} transmissions." });
            }
            return Ok(await _db.QueryTransmissionsAsync(new TransmissionQuery { Ids = idList, Limit = idList.Count }));
        }

        TransmissionCursor? position = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!TransmissionCursor.TryDecode(cursor, out var decoded))
            {
                return BadRequest(new { error = "cursor is not a value returned by this endpoint." });
            }
            position = decoded;
        }

        string? sinceUtc = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (!ApiDates.TryParseUtc(since, out var s)) return BadRequest(new { error = "since must be an ISO 8601 date/time." });
            sinceUtc = s.ToString("o");
        }
        string? untilUtc = null;
        if (!string.IsNullOrWhiteSpace(until))
        {
            if (!ApiDates.TryParseUtc(until, out var u)) return BadRequest(new { error = "until must be an ISO 8601 date/time." });
            untilUtc = u.ToString("o");
        }

        var query = new TransmissionQuery
        {
            Limit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit),
            Cursor = position,
            SinceUtc = sinceUtc,
            UntilUtc = untilUtc,
            Channel = string.IsNullOrWhiteSpace(channel) ? null : channel,
            Frequency = frequency,
            Search = string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
            FavoritesOnly = favorites,
            TonesOnly = tones,
            IncidentsOnly = incidents,
        };
        return Ok(await _db.QueryTransmissionsAsync(query));
    }
}
