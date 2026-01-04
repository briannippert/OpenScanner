SELECT id, timestamp, frequency, alphaTag, description, lat, lon, alt, audio_path as AudioPath, duration, transcription, sourceID, targetID 
FROM transmissions 
WHERE strftime('%Y', timestamp) = @Year 
  AND strftime('%m', timestamp) = @Month 
  AND strftime('%d', timestamp) = @Day
  AND alphaTag = @AlphaTag
  AND frequency = @Frequency
ORDER BY timestamp DESC
