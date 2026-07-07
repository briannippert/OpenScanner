using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests;

public class ChannelServiceTests
{
    private readonly Mock<IDatabase> _db = new();
    private readonly ILogger<ChannelService> _logger =
        LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Critical)).CreateLogger<ChannelService>();

    private static readonly Channel[] AllChannels =
    {
        new(155.000, "A", "Chan A"),
        new(156.000, "B", "Chan B"),
    };

    private static readonly Channel[] LocalChannels =
    {
        new(154.000, "Local", "Nearby"),
    };

    [Fact]
    public async Task Constructor_LoadsChannelsFromDatabase()
    {
        _db.Setup(d => d.GetAllChannelsAsync()).ReturnsAsync(AllChannels);

        var service = new ChannelService(_db.Object, _logger);

        await WaitFor(() => service.Channels.Count == 2);
        Assert.Contains(service.Channels, c => c.Frequency == 155.000);
    }

    [Fact]
    public async Task CheckGeoRefresh_FirstFix_LoadsNearbyChannels()
    {
        _db.Setup(d => d.GetAllChannelsAsync()).ReturnsAsync(AllChannels);
        _db.Setup(d => d.GetChannelsNearAsync(It.IsAny<double>(), It.IsAny<double>()))
           .ReturnsAsync(LocalChannels);
        var service = new ChannelService(_db.Object, _logger);
        await WaitFor(() => service.Channels.Count == 2);

        service.CheckGeoRefresh(43.0, -71.0);

        await WaitFor(() => service.Channels.Count == 1 && service.Channels[0].AlphaTag == "Local");
        _db.Verify(d => d.GetChannelsNearAsync(43.0, -71.0), Times.Once);
    }

    [Fact]
    public async Task CheckGeoRefresh_SmallMove_DoesNotReload()
    {
        _db.Setup(d => d.GetAllChannelsAsync()).ReturnsAsync(AllChannels);
        _db.Setup(d => d.GetChannelsNearAsync(It.IsAny<double>(), It.IsAny<double>()))
           .ReturnsAsync(LocalChannels);
        var service = new ChannelService(_db.Object, _logger);
        await WaitFor(() => service.Channels.Count == 2);

        service.CheckGeoRefresh(43.0, -71.0);           // first fix -> refresh
        await WaitFor(() => service.Channels.Count == 1);
        service.CheckGeoRefresh(43.0001, -71.0001);      // ~30 ft away, under 1 mile

        await Task.Delay(50); // give any erroneous refresh a chance to happen
        _db.Verify(d => d.GetChannelsNearAsync(It.IsAny<double>(), It.IsAny<double>()), Times.Once);
    }

    [Fact]
    public async Task CheckGeoRefresh_LargeMove_ReloadsAgain()
    {
        // Record each nearby-lookup so we can wait for the fire-and-forget refreshes
        // to actually run before verifying, instead of racing them.
        var calls = new System.Collections.Concurrent.ConcurrentBag<(double Lat, double Lon)>();
        _db.Setup(d => d.GetAllChannelsAsync()).ReturnsAsync(AllChannels);
        _db.Setup(d => d.GetChannelsNearAsync(It.IsAny<double>(), It.IsAny<double>()))
           .ReturnsAsync(LocalChannels)
           .Callback<double, double>((lat, lon) => calls.Add((lat, lon)));
        var service = new ChannelService(_db.Object, _logger);
        await WaitFor(() => service.Channels.Count == 2);

        service.CheckGeoRefresh(43.0, -71.0);            // first fix
        service.CheckGeoRefresh(44.0, -72.0);            // ~85 miles away

        await WaitFor(() => calls.Contains((43.0, -71.0)) && calls.Contains((44.0, -72.0)));
        _db.Verify(d => d.GetChannelsNearAsync(43.0, -71.0), Times.Once);
        _db.Verify(d => d.GetChannelsNearAsync(44.0, -72.0), Times.Once);
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(5);
        Assert.True(condition(), "condition not met within timeout");
    }
}
