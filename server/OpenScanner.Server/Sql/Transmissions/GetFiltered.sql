SELECT id, timestamp, frequency, alphaTag, description, lat, lon, alt, audio_path as AudioPath, duration, transcription, sourceID, targetID 
FROM transmissions 
WHERE strftime('%Y', timestamp, 'localtime') = @Year 
  AND strftime('%m', timestamp, 'localtime') = @Month 
  AND strftime('%d', timestamp, 'localtime') = @Day
  AND alphaTag = @AlphaTag
  AND frequency = @Frequency
ORDER BY timestamp DESC