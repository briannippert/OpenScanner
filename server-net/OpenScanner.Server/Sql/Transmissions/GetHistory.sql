SELECT id, timestamp, frequency, alphaTag, description, lat, lon, alt, audio_path as AudioPath, duration, transcription, sourceID, targetID FROM transmissions ORDER BY timestamp DESC LIMIT @Limit
