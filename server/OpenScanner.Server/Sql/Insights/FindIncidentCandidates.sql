-- Geolocated incidents from the same service inside a lat/lon box and time window, which
-- may be the same incident as a new call. The caller narrows the box to a true radius.
-- `IS` makes a null service match a null service. Timestamps are round-trip ("o") UTC
-- strings, so lexicographic comparison is chronological.
SELECT i.transmissionId, i.incidentId, i.lat, i.lon, t.timestamp
FROM transmission_insights i
JOIN transmissions t ON t.id = i.transmissionId
WHERE i.isIncident = 1
  AND i.incidentId IS NOT NULL
  AND i.service IS @Service
  AND i.lat BETWEEN @MinLat AND @MaxLat
  AND i.lon BETWEEN @MinLon AND @MaxLon
  AND t.timestamp >= @Since AND t.timestamp <= @Until
  AND i.transmissionId != @ExcludeId
