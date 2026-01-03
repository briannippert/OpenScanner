using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests;

public class GpsServiceTests
{
    private readonly Mock<ILogger<GpsService>> _loggerMock = new();

    [Fact]
    public void ParseTpv_ShouldUpdateLocation()
    {
        var service = new GpsService(_loggerMock.Object);
        var json = @"{
            ""class"": ""TPV"",
            ""mode"": 3,
            ""lat"": 45.1234,
            ""lon"": -122.5678,
            ""alt"": 100.5,
            ""speed"": 10.2,
            ""time"": ""2026-01-03T12:00:00Z""
        }";
        var doc = JsonDocument.Parse(json);

        GpsData? receivedData = null;
        service.OnGpsUpdate += (data) => receivedData = data;

        service.ParseTpv(doc.RootElement);

        Assert.NotNull(receivedData);
        Assert.Equal(45.1234, receivedData.Lat);
        Assert.Equal(-122.5678, receivedData.Lon);
        Assert.Equal(100.5, receivedData.Alt);
        Assert.Equal(3, receivedData.Fix);
    }

    [Fact]
    public void ParseSky_ShouldUpdateSats()
    {
        var service = new GpsService(_loggerMock.Object);
        var json = @"{
            ""class"": ""SKY"",
            ""satellites"": [
                {""used"": true},
                {""used"": true},
                {""used"": false},
                {""used"": true}
            ]
        }";
        var doc = JsonDocument.Parse(json);

        GpsData? receivedData = null;
        service.OnGpsUpdate += (data) => receivedData = data;

        service.ParseSky(doc.RootElement);

        Assert.NotNull(receivedData);
        Assert.Equal(3, receivedData.Sats);
    }
}
