using Microsoft.AspNetCore.Mvc;
using OpenScanner.Server.Interfaces;

namespace OpenScanner.Server.Controllers;

/// <summary>
/// Controller for support and system information.
/// </summary>
[ApiController]
[Route("api")]
[Produces("application/json")]
public class SupportController : ControllerBase
{
    private readonly ISupportService _support;
    private readonly ILogger<SupportController> _logger;

    public SupportController(ISupportService support, ILogger<SupportController> logger)
    {
        _support = support;
        _logger = logger;
    }

    /// <summary>
    /// Retrieves system information including version and git commit.
    /// </summary>
    /// <returns>System information dictionary.</returns>
    [HttpGet("system/info")]
    [ProducesResponseType(typeof(Dictionary<string, string>), StatusCodes.Status200OK)]
    public IActionResult GetSystemInfo()
    {
        return Ok(_support.GetVersionInfo());
    }

    /// <summary>
    /// Retrieves storage usage statistics: recordings size/count, database size, and disk space.
    /// </summary>
    /// <returns>Storage usage snapshot.</returns>
    [HttpGet("system/storage")]
    [ProducesResponseType(typeof(OpenScanner.Server.Models.StorageInfo), StatusCodes.Status200OK)]
    public IActionResult GetStorageInfo()
    {
        return Ok(_support.GetStorageInfo());
    }

    /// <summary>
    /// Retrieves an instantaneous CPU/memory/transcription-queue sample. The debug
    /// page polls this once per second to build rolling resource graphs.
    /// </summary>
    /// <returns>A <see cref="OpenScanner.Server.Models.SystemStats"/> sample.</returns>
    [HttpGet("system/stats")]
    [ProducesResponseType(typeof(OpenScanner.Server.Models.SystemStats), StatusCodes.Status200OK)]
    public IActionResult GetSystemStats()
    {
        return Ok(_support.GetSystemStats());
    }

    /// <summary>
    /// Retrieves the running services and the TCP ports the host is listening on.
    /// </summary>
    /// <returns>A <see cref="OpenScanner.Server.Models.ServicesSnapshot"/>.</returns>
    [HttpGet("system/services")]
    [ProducesResponseType(typeof(OpenScanner.Server.Models.ServicesSnapshot), StatusCodes.Status200OK)]
    public IActionResult GetServices()
    {
        return Ok(_support.GetServices());
    }

    /// <summary>
    /// Retrieves a composite diagnostics snapshot (scanner, GPS, database, connections,
    /// recording activity, and reliability counters) for the debug page.
    /// </summary>
    /// <returns>A <see cref="OpenScanner.Server.Models.DiagnosticsSnapshot"/>.</returns>
    [HttpGet("system/diagnostics")]
    [ProducesResponseType(typeof(OpenScanner.Server.Models.DiagnosticsSnapshot), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDiagnostics()
    {
        return Ok(await _support.GetDiagnosticsAsync());
    }

    /// <summary>
    /// Retrieves the most recent OpenScanner systemd journal lines.
    /// </summary>
    /// <param name="lines">Number of trailing lines to return (default 500).</param>
    /// <returns>Plain-text log content.</returns>
    [HttpGet("system/logs")]
    [Produces("text/plain")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLogs([FromQuery] int lines = 500)
    {
        var logs = await _support.GetSystemdLogsAsync(lines);
        return Content(logs, "text/plain");
    }

    /// <summary>
    /// Generates and downloads a support package containing logs, configuration, and system info.
    /// </summary>
    /// <returns>A ZIP file containing diagnostic information.</returns>
    [HttpGet("support/package")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSupportPackage()
    {
        _logger.LogInformation("Generating support package...");
        try
        {
            var data = await _support.CreateSupportPackageAsync();
            var filename = $"openscanner_support_{DateTime.UtcNow:yyyyMMdd_HHmm}.zip";
            return File(data, "application/zip", filename);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate support package");
            return StatusCode(500, "Internal server error while generating support package");
        }
    }
}
