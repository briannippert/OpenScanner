using OpenScanner.Server.Models;

namespace OpenScanner.Server.Interfaces;

/// <summary>
/// On-demand, low-priority job that re-transcribes recordings from the last 24
/// hours that are missing a transcription. It yields to live transcription work.
/// </summary>
public interface IBackfillService
{
    /// <summary>Gets the current progress/status of the backfill job.</summary>
    BackfillStatus GetStatus();

    /// <summary>Starts the job. Returns false if it is already running.</summary>
    bool Start();

    /// <summary>Requests the running job to stop after the current clip.</summary>
    void Stop();
}
