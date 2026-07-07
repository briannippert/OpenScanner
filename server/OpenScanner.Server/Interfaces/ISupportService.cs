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
    /// Creates a ZIP archive containing diagnostic information.
    /// </summary>
    /// <returns>Byte array of the ZIP file.</returns>
    Task<byte[]> CreateSupportPackageAsync();
}
