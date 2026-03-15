SELECT id, timestamp, frequency, alphaTag, description, lat, lon, alt, audio_path as AudioPath, duration, transcription, sourceID, targetID, isFavorite 
FROM transmissions 
WHERE transcription LIKE @Query 
   OR description LIKE @Query 
   OR alphaTag LIKE @Query 
   OR CAST(frequency AS TEXT) LIKE @Query
ORDER BY timestamp DESC
LIMIT 100
