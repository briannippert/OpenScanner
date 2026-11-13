using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests.Services;

public class IncidentGroupingTests
{
    private static readonly DateTime When = new(2026, 11, 12, 14, 0, 0, DateTimeKind.Utc);

    private static IncidentCandidate C(string incident, double lat, double lon, double minutesFromWhen) => new()
    {
        TransmissionId = incident + "-tx", IncidentId = incident, Lat = lat, Lon = lon,
        Timestamp = When.AddMinutes(minutesFromWhen).ToString("o"),
    };

    [Fact]
    public void DistanceMeters_IsAboutRight()
    {
        // 0.001 degrees of latitude is ~111 m.
        Assert.InRange(IncidentGrouping.DistanceMeters(42.78, -71.23, 42.781, -71.23), 109, 113);
        Assert.Equal(0, IncidentGrouping.DistanceMeters(42.78, -71.23, 42.78, -71.23), 6);
    }

    [Fact]
    public void Pick_MatchesNearbyCallWithinWindow()
    {
        Assert.Equal("inc", IncidentGrouping.Pick(new[] { C("inc", 42.7803, -71.2302, -10) }, 42.78, -71.23, When));
    }

    [Fact]
    public void Pick_IgnoresCallsTooFarOrTooLongAgo()
    {
        var candidates = new[]
        {
            C("far", 42.79, -71.23, -1),     // ~1.1 km away
            C("old", 42.78, -71.23, -16),    // outside 15 min
            C("later", 42.78, -71.23, 16),   // outside 15 min the other way
        };
        Assert.Null(IncidentGrouping.Pick(candidates, 42.78, -71.23, When));
    }

    [Fact]
    public void Pick_PrefersTheClosestInTime()
    {
        var candidates = new[] { C("earlier", 42.78, -71.23, -12), C("recent", 42.78, -71.23, -2), C("after", 42.78, -71.23, 5) };
        Assert.Equal("recent", IncidentGrouping.Pick(candidates, 42.78, -71.23, When));
    }
}
