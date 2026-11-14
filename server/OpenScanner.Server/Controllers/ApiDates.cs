using System.Globalization;

namespace OpenScanner.Server.Controllers;

/// <summary>Parsing for the ISO 8601 date/time query parameters the API accepts.</summary>
internal static class ApiDates
{
    /// <summary>
    /// Parses an ISO 8601 value as UTC. A value without an offset is taken to be UTC.
    /// </summary>
    public static bool TryParseUtc(string value, out DateTime utc)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            utc = dto.UtcDateTime;
            return true;
        }
        utc = default;
        return false;
    }
}
