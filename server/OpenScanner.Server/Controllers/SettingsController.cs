using Microsoft.AspNetCore.Mvc;
using OpenScanner.Server.Interfaces;

namespace OpenScanner.Server.Controllers;

/// <summary>
/// Controller for managing system settings.
/// </summary>
[ApiController]
[Route("api/settings")]
[Produces("application/json")]
public class SettingsController : ControllerBase
{
    private readonly IDatabase _db;
    private readonly ITranscriptionService _transcription;

    public SettingsController(IDatabase db, ITranscriptionService transcription)
    {
        _db = db;
        _transcription = transcription;
    }

    /// <summary>
    /// Retrieves all system settings.
    /// </summary>
    /// <returns>Dictionary of key-value settings.</returns>
    [HttpGet("")]
    [ProducesResponseType(typeof(Dictionary<string, string>), StatusCodes.Status200OK)]
    public async Task<Dictionary<string, string>> GetSettings()
    {
        return await _db.GetAllSettingsAsync();
    }

    /// <summary>
    /// Updates a specific system setting.
    /// </summary>
    /// <param name="key">The setting key (e.g., "EnableTranscription").</param>
    /// <param name="value">The new value.</param>
    [HttpPost("{key}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateSetting(string key, [FromBody] string value)
    {
        await _db.SetSettingAsync(key, value);

        // Changing the transcription model kicks off a background download of
        // the ggml weights if they aren't present yet.
        if (key == "TranscriptionModel" && !string.IsNullOrWhiteSpace(value))
        {
            _transcription.PrepareModel(value);
        }

        return Ok();
    }
}
