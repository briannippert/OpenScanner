using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

public class ChannelService : IChannelService
{
    private readonly IDatabase _db;
    private readonly ILogger<ChannelService> _logger;
    private (double Lat, double Lon)? _lastGeoPosition;

    public List<Channel> Channels { get; private set; } = new();

    public ChannelService(IDatabase db, ILogger<ChannelService> logger)
    {
        _db = db;
        _logger = logger;
        ReloadChannels();
    }

    public void ReloadChannels()
    {
        Task.Run(async () => {
            Channels = (await _db.GetAllChannelsAsync()).ToList();
            _logger.LogInformation($"Loaded {Channels.Count} channels.");
        });
    }

    public void CheckGeoRefresh(double lat, double lon)
    {
        if (!_lastGeoPosition.HasValue)
        {
            _lastGeoPosition = (lat, lon);
            RefreshGeoChannels(lat, lon);
            return;
        }

        // Only refresh if moved > 1 mile
        double dist = CalculateDistance(_lastGeoPosition.Value.Lat, _lastGeoPosition.Value.Lon, lat, lon);
        if (dist > 1.0)
        {
            _lastGeoPosition = (lat, lon);
            RefreshGeoChannels(lat, lon);
        }
    }

    private void RefreshGeoChannels(double lat, double lon)
    {
        Task.Run(async () => {
            var localChannels = (await _db.GetChannelsNearAsync(lat, lon)).ToList();
            if (localChannels.Count > 0)
            {
                _logger.LogInformation($"Geo-Sync: Found {localChannels.Count} local channels.");
                Channels = localChannels; 
            }
        });
    }

    private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        var d1 = lat1 * (Math.PI / 180.0);
        var num1 = lon1 * (Math.PI / 180.0);
        var d2 = lat2 * (Math.PI / 180.0);
        var num2 = lon2 * (Math.PI / 180.0) - num1;
        var d3 = Math.Pow(Math.Sin((d2 - d1) / 2.0), 2.0) + Math.Cos(d1) * Math.Cos(d2) * Math.Pow(Math.Sin(num2 / 2.0), 2.0);
        return 6376500.0 * (2.0 * Math.Atan2(Math.Sqrt(d3), Math.Sqrt(1.0 - d3))) * 0.000621371;
    }
}
