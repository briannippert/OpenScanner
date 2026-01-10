using Microsoft.AspNetCore.Mvc;
using OpenScanner.Server.Services;
using OpenScanner.Server.Interfaces;

namespace OpenScanner.Server.Controllers;

/// <summary>
/// Controller for support and diagnostic operations.
/// </summary>
[ApiController]
[Route("api/support")]
public class SupportController : ControllerBase
{
    private readonly ISupportService _supportService;
    private readonly ILogger<SupportController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SupportController"/> class.
    /// </summary>
    /// <param name="supportService">The support service.</param>
    /// <param name="logger">The logger instance.</param>
    public SupportController(ISupportService supportService, ILogger<SupportController> logger)
    {
        _supportService = supportService;
        _logger = logger;
    }

    /// <summary>
    /// Generates and downloads a support package containing logs, configuration, and system info.
    /// </summary>
    /// <returns>A ZIP file containing diagnostic information.</returns>
    [HttpGet("package")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSupportPackage()
    {
        _logger.LogInformation("Generating support package...");
        try
        {
            var data = await _supportService.CreateSupportPackageAsync();
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
