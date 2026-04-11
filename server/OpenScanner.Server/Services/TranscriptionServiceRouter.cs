using OpenScanner.Server.Interfaces;

namespace OpenScanner.Server.Services;

/// <summary>
/// Routes transcription requests to either the local whisper.cpp service or a
/// remote server based on the TranscriptionMode database setting.
/// </summary>
public class TranscriptionServiceRouter : ITranscriptionService
{
    private readonly WhisperTranscriptionService _localService;
    private readonly RemoteWhisperTranscriptionService _remoteService;
    private readonly IDatabase _db;
    private readonly ILogger<TranscriptionServiceRouter> _logger;

    public TranscriptionServiceRouter(
        WhisperTranscriptionService localService,
        RemoteWhisperTranscriptionService remoteService,
        IDatabase db,
        ILogger<TranscriptionServiceRouter> logger)
    {
        _localService = localService;
        _remoteService = remoteService;
        _db = db;
        _logger = logger;
    }

    public string? TranscribeAudio(string audioPath)
    {
        var mode = _db.GetSettingAsync("TranscriptionMode").GetAwaiter().GetResult() ?? "local";

        if (mode.Equals("remote", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Using remote transcription service");
            return _remoteService.TranscribeAudio(audioPath);
        }

        _logger.LogDebug("Using local transcription service");
        return _localService.TranscribeAudio(audioPath);
    }
}
