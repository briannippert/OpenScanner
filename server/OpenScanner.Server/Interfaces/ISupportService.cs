using System.Collections.Generic;
using System.Threading.Tasks;

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
    /// Creates a ZIP archive containing diagnostic information.
    /// </summary>
    /// <returns>Byte array of the ZIP file.</returns>
    Task<byte[]> CreateSupportPackageAsync();
}
