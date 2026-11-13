using System.Globalization;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

/// <summary>
/// Decides which incident a geolocated call belongs to, so several transmissions about
/// one incident (dispatch, en route, on scene) show as a single map marker. Two calls
/// are the same incident when they are from the same service (police and fire responding
/// to one address stay separate), within <see cref="RadiusMeters"/> of each other, and
/// within <see cref="Window"/> of each other. Matching any call in a group lets a long
/// incident chain along.
/// </summary>
public static class IncidentGrouping
{
    /// <summary>How far apart two geocoded points can be and still be the same incident.</summary>
    public const double RadiusMeters = 100;

    /// <summary>How far apart in time two calls can be and still be the same incident.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    // Half-width of the SQL pre-filter box in degrees of latitude (~111 m), just over the radius.
    private const double BoxDegrees = 0.001;

    /// <summary>Great-circle distance in metres.</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000;
        static double Rad(double d) => d * Math.PI / 180.0;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>
    /// The incident id of the candidate within <see cref="RadiusMeters"/> that is closest in
    /// time to <paramref name="when"/>, or null when none is close enough.
    /// </summary>
    public static string? Pick(IEnumerable<IncidentCandidate> candidates, double lat, double lon, DateTime when)
    {
        string? best = null;
        var bestGap = TimeSpan.MaxValue;
        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c.IncidentId)) continue;
            if (DistanceMeters(lat, lon, c.Lat, c.Lon) > RadiusMeters) continue;
            if (!TryParseUtc(c.Timestamp, out var t)) continue;
            var gap = (t - when).Duration();
            if (gap > Window || gap >= bestGap) continue;
            best = c.IncidentId;
            bestGap = gap;
        }
        return best;
    }

    /// <summary>
    /// The incident id for a geolocated call: an existing nearby incident's id, or the call's
    /// own transmission id when it starts a new incident.
    /// </summary>
    public static async Task<string> ResolveAsync(IDatabase db, string transmissionId, string? service, double lat, double lon, string timestampUtc)
    {
        if (!TryParseUtc(timestampUtc, out var when)) return transmissionId;
        var candidates = await db.FindIncidentCandidatesAsync(
            service, lat, lon, BoxDegrees,
            (when - Window).ToString("o", CultureInfo.InvariantCulture),
            (when + Window).ToString("o", CultureInfo.InvariantCulture),
            transmissionId);
        return Pick(candidates, lat, lon, when) ?? transmissionId;
    }

    private static bool TryParseUtc(string? value, out DateTime utc)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc))
        {
            return true;
        }
        utc = default;
        return false;
    }
}
