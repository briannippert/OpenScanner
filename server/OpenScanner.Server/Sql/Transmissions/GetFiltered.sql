-- One channel's transmissions for one local day. @Start/@End are the day's bounds in
-- round-trip UTC, so this uses the timestamp index instead of strftime on every row.
SELECT id, timestamp, frequency, alphaTag, description, lat, lon, alt, audio_path as AudioPath, duration, transcription, sourceID, targetID, detectedTone, speakerChain, isFavorite
FROM transmissions
WHERE timestamp >= @Start
  AND timestamp < @End
  AND alphaTag = @AlphaTag
  AND ABS(frequency - @Frequency) < 0.0005
ORDER BY timestamp DESC
