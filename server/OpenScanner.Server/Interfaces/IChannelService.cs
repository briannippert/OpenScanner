using OpenScanner.Server.Models;

namespace OpenScanner.Server.Interfaces;

public interface IChannelService
{
    List<Channel> Channels { get; }
    void ReloadChannels();

    /// <summary>
    /// Raised after <see cref="Channels"/> is replaced, whether by a reload or by a GPS
    /// geo-refresh. Loading is asynchronous, so this is the reliable signal that the new
    /// list is in place.
    /// </summary>
    event Action? ChannelsChanged;
    void CheckGeoRefresh(double lat, double lon);
}
