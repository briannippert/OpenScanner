SELECT id, timestamp, frequency, alphaTag, description, lat, lon, alt, audio_path as AudioPath, duration, transcription, sourceID, targetID, detectedTone, speakerChain, isFavorite
FROM transmissions
WHERE isFavorite = 1
ORDER BY timestamp DESC
