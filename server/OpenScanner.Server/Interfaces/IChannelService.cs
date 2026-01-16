using OpenScanner.Server.Models;

namespace OpenScanner.Server.Interfaces;

public interface IChannelService
{
    List<Channel> Channels { get; }
    void ReloadChannels();
    void CheckGeoRefresh(double lat, double lon);
}
