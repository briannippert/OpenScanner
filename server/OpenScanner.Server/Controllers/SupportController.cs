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
