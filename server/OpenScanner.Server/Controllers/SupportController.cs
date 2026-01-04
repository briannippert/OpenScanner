using Microsoft.AspNetCore.Mvc;
using OpenScanner.Server.Services;

namespace OpenScanner.Server.Controllers;

[ApiController]
[Route("api/support")]
public class SupportController : ControllerBase
{
    private readonly SupportService _supportService;
    private readonly ILogger<SupportController> _logger;

    public SupportController(SupportService supportService, ILogger<SupportController> logger)
    {
        _supportService = supportService;
        _logger = logger;
    }

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
