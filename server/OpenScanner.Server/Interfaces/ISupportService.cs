using System.Collections.Generic;
using System.Threading.Tasks;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Interfaces;

/// <summary>
/// Interface for support and diagnostic services.
/// </summary>
public interface ISupportService
{
    /// <summary>
    /// Gets version information including the Git commit hash.
    /// </summary>
    /// <returns>A dictionary containing version info.</returns>
    Dictionary<string, string> GetVersionInfo();

    /// <summary>
    /// Gets storage usage statistics: recordings size/count, database size, and disk space.
    /// </summary>
    /// <returns>A <see cref="StorageInfo"/> snapshot.</returns>
    StorageInfo GetStorageInfo();

    /// <summary>
    /// Gets an instantaneous CPU/memory/transcription-queue sample for the debug page.
    /// </summary>
    SystemStats GetSystemStats();

    /// <summary>
    /// Gets the running services and listening TCP ports on the host.
    /// </summary>
    ServicesSnapshot GetServices();

    /// <summary>
    /// Gets a composite, slower-changing diagnostics snapshot (scanner, GPS, DB,
    /// connections, recording activity, reliability counters) for the debug page.
    /// </summary>
    Task<DiagnosticsSnapshot> GetDiagnosticsAsync();

    /// <summary>
    /// Gets the most recent OpenScanner systemd journal lines (falls back to the
    /// in-memory log buffer when journalctl is unavailable).
    /// </summary>
    /// <param name="lines">Maximum number of trailing lines to return.</param>
    Task<string> GetSystemdLogsAsync(int lines);

    /// <summary>
    /// Creates a ZIP archive containing diagnostic information.
    /// </summary>
    /// <returns>Byte array of the ZIP file.</returns>
    Task<byte[]> CreateSupportPackageAsync();
}
