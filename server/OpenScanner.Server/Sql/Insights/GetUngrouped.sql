-- Geolocated incidents saved before incident grouping existed, oldest first.
SELECT i.transmissionId, i.service, i.lat, i.lon, t.timestamp
FROM transmission_insights i
JOIN transmissions t ON t.id = i.transmissionId
WHERE i.isIncident = 1
  AND i.incidentId IS NULL
  AND i.lat IS NOT NULL AND i.lon IS NOT NULL
ORDER BY t.timestamp ASC
