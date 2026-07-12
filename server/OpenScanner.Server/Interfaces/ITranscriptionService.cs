using System;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Interfaces;

public interface ITranscriptionService
{
    string? TranscribeAudio(string audioPath);
    void QueueTranscription(CallLog log, string audioPath);

    /// <summary>
    /// Current depth of the transcription queue and the number of active workers.
    /// </summary>
    TranscriptionQueueStatus GetQueueStatus();

    /// <summary>
    /// Transcribes an existing recording synchronously (not via the live queue),
    /// persists the result, and raises <see cref="OnTranscriptionCompleted"/> so the
    /// UI updates. Used by the backfill job. Returns true if text was produced.
    /// </summary>
    Task<bool> RetranscribeAsync(CallLog log, string audioPath);

    /// <summary>
    /// Ensure the given whisper model is downloaded, in the background. Safe to
    /// call when it already exists. Progress is surfaced via the
    /// TranscriptionModelStatus setting.
    /// </summary>
    void PrepareModel(string modelName);

    event Action<CallLog>? OnTranscriptionCompleted;
}