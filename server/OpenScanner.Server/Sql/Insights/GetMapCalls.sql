-- Geolocated incidents for the map. Timestamps are round-trip ("o") UTC strings, so
-- lexicographic comparison is chronological. The callType/service filters are
-- appended by Database.GetMapCallsAsync when set.
SELECT t.id, t.timestamp, t.frequency, t.alphaTag, t.audio_path AS AudioPath, t.duration, t.transcription,
       i.callType, i.service, i.summary, i.incidentStatus, i.address, i.placeName, i.matchedAddress, i.lat, i.lon
FROM transmission_insights i
JOIN transmissions t ON t.id = i.transmissionId
WHERE i.isIncident = 1
  AND i.lat IS NOT NULL AND i.lon IS NOT NULL
  AND t.timestamp >= @Since
  AND (@Until IS NULL OR t.timestamp < @Until)
