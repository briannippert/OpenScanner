SELECT id, timestamp, frequency, alphaTag, description, lat, lon, alt, audio_path as AudioPath, duration, transcription, sourceID, targetID, detectedTone, speakerChain, isFavorite
FROM transmissions
WHERE (transcription IS NULL OR transcription = '')
  AND timestamp >= @Since
ORDER BY timestamp DESC
