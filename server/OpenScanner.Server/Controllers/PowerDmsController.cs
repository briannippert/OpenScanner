using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenScanner.Server.Interfaces;

namespace OpenScanner.Server.Controllers;

/// <summary>
/// Controller for PowerDMS daily log integration.
/// Proxies PDF documents from the PowerDMS public API to work around browser CORS restrictions.
/// </summary>
[ApiController]
[Route("api/powerdms")]
public class PowerDmsController : ControllerBase
{
    private static readonly HttpClient _http = new();
    private static List<PowerDmsDocument>? _cachedDocs;
    private static DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private static readonly SemaphoreSlim _cacheLock = new(1, 1);

    private readonly IConfiguration _config;
    private readonly IDatabase _db;
    private readonly ILogger<PowerDmsController> _logger;

    public PowerDmsController(IConfiguration config, IDatabase db, ILogger<PowerDmsController> logger)
    {
        _config = config;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// The PowerDMS department slug, managed from the web-app settings (DB),
    /// falling back to appsettings. Returns null when not configured.
    /// </summary>
    private async Task<string?> GetDepartmentAsync()
    {
        var fromDb = await _db.GetSettingAsync("PowerDmsDepartment");
        if (!string.IsNullOrWhiteSpace(fromDb)) return fromDb.Trim();
        return _config["PowerDMS:Department"] is { Length: > 0 } d ? d : null;
    }

    /// <summary>
    /// Returns the configured PowerDMS department slug, or null if not configured.
    /// The client uses this to decide whether to show the daily log button.
    /// </summary>
    [HttpGet("config")]
    [ProducesResponseType(typeof(PowerDmsConfigResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConfig()
    {
        return Ok(new PowerDmsConfigResponse(await GetDepartmentAsync()));
    }

    /// <summary>
    /// Checks whether a daily log exists in PowerDMS for the given date without downloading the PDF.
    /// Uses the same cached document list as the proxy endpoint.
    /// </summary>
    [HttpGet("check/{year}/{month}/{day}")]
    [ProducesResponseType(typeof(PowerDmsCheckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CheckDailyLog(int year, int month, int day)
    {
        var dept = await GetDepartmentAsync();
        if (dept is null)
            return Ok(new PowerDmsCheckResponse(false));

        DateOnly targetDate;
        try { targetDate = new DateOnly(year, month, day); }
        catch { return Ok(new PowerDmsCheckResponse(false)); }

        List<PowerDmsDocument> docs;
        try
        {
            docs = await GetCachedDocumentsAsync(dept);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch PowerDMS document list for check ({Department})", dept);
            return StatusCode(503, "Failed to retrieve PowerDMS document list.");
        }

        var exists = docs.Any(d =>
            d.Name.Contains(targetDate.ToString("MMMM d, yyyy"), StringComparison.OrdinalIgnoreCase)
            && d.Name.Contains("Daily Log", StringComparison.OrdinalIgnoreCase));

        return Ok(new PowerDmsCheckResponse(exists));
    }

    /// <summary>
    /// Streams the PowerDMS daily log PDF for the given date.
    /// Fetches the document list (cached for 1 hour), finds the matching entry, and proxies the PDF.
    /// </summary>
    [HttpGet("daily-log/{year}/{month}/{day}")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetDailyLog(int year, int month, int day)
    {
        var dept = await GetDepartmentAsync();
        if (dept is null)
            return StatusCode(503, "PowerDMS integration is not configured.");

        var targetDate = new DateOnly(year, month, day);
        var docName = $"{targetDate.ToString("MMMM d, yyyy")} Daily Log";

        List<PowerDmsDocument> docs;
        try
        {
            docs = await GetCachedDocumentsAsync(dept);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch PowerDMS document list for {Department}", dept);
            return StatusCode(503, "Failed to retrieve PowerDMS document list.");
        }

        // Find the most recent match for this date (highest ID wins in case of duplicates)
        var doc = docs
            .Where(d => d.Name.Contains(targetDate.ToString("MMMM d, yyyy"), StringComparison.OrdinalIgnoreCase)
                     && d.Name.Contains("Daily Log", StringComparison.OrdinalIgnoreCase))
            .MaxBy(d => d.Id);

        if (doc is null)
            return NotFound($"No daily log found for {targetDate:MMMM d, yyyy}.");

        var pdfUrl = $"https://public.powerdms.com/{dept}/documents/{doc.Id}/download";
        HttpResponseMessage pdfResponse;
        try
        {
            pdfResponse = await _http.GetAsync(pdfUrl, HttpCompletionOption.ResponseHeadersRead);
            pdfResponse.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy PowerDMS PDF for document {Id}", doc.Id);
            return StatusCode(503, "Failed to retrieve PDF from PowerDMS.");
        }

        var stream = await pdfResponse.Content.ReadAsStreamAsync();
        return File(stream, "application/pdf", $"DailyLog-{year}-{month:D2}-{day:D2}.pdf");
    }

    private async Task<List<PowerDmsDocument>> GetCachedDocumentsAsync(string dept)
    {
        if (_cachedDocs is not null && DateTime.UtcNow < _cacheExpiry)
            return _cachedDocs;

        await _cacheLock.WaitAsync();
        try
        {
            // Double-check inside the lock
            if (_cachedDocs is not null && DateTime.UtcNow < _cacheExpiry)
                return _cachedDocs;

            var url = $"https://public.powerdms.com/{dept}/documents";
            var response = await _http.GetFromJsonAsync<PowerDmsDocumentListResponse>(url)
                ?? throw new InvalidOperationException("PowerDMS returned null document list.");

            _cachedDocs = response.Data ?? throw new InvalidOperationException("PowerDMS document list 'data' field was null.");
            _cacheExpiry = DateTime.UtcNow.Add(CacheTtl);
            return _cachedDocs;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}

public record PowerDmsConfigResponse(string? Department);

public record PowerDmsCheckResponse(bool Exists);

public record PowerDmsDocument(string Id, string Name, string PublicUrl);

/// <summary>Wrapper for the PowerDMS API envelope: { "data": [...], "error": ... }</summary>
public record PowerDmsDocumentListResponse([property: JsonPropertyName("data")] List<PowerDmsDocument>? Data);
