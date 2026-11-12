-- Transcribed transmissions with no AI analysis yet, newest first.
SELECT t.id, t.timestamp, t.frequency, t.alphaTag, t.description, t.audio_path AS AudioPath, t.duration, t.transcription
FROM transmissions t
LEFT JOIN transmission_insights i ON i.transmissionId = t.id
WHERE i.transmissionId IS NULL
  AND t.transcription IS NOT NULL AND t.transcription != ''
ORDER BY t.timestamp DESC
LIMIT @Limit
