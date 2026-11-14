-- Column list for GET /api/transmissions. Database.QueryTransmissionsAsync appends the
-- WHERE filters, ORDER BY t.timestamp DESC, t.id DESC and the LIMIT.
SELECT t.id, t.timestamp, t.frequency, t.alphaTag, t.description, t.lat, t.lon, t.alt, t.audio_path AS AudioPath,
       t.duration, t.transcription, t.sourceID, t.targetID, t.detectedTone, t.speakerChain, t.isFavorite
FROM transmissions t
