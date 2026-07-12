using OpenScanner.Server.Models;

namespace OpenScanner.Server.Interfaces;

/// <summary>
/// Drives the in-UI self-updater: checks GitHub for a newer release and applies it
/// (git reset to the release tag, rebuild, restart) while streaming progress.
/// </summary>
public interface IUpdateService
{
    /// <summary>Returns the current snapshot (state, versions, availability, and log).</summary>
    UpdateStatus GetStatus();

    /// <summary>
    /// Checks the latest GitHub release against the running checkout and updates the
    /// cached availability. Safe to call periodically; skipped while an update runs.
    /// </summary>
    Task<UpdateStatus> CheckAsync(bool force);

    /// <summary>
    /// Starts an update in the background if one is not already running.
    /// Returns false if an update is already in progress.
    /// </summary>
    bool TryStartUpdate();
}
