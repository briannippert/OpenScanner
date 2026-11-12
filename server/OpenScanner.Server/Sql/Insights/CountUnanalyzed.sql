SELECT COUNT(*)
FROM transmissions t
LEFT JOIN transmission_insights i ON i.transmissionId = t.id
WHERE i.transmissionId IS NULL
  AND t.transcription IS NOT NULL AND t.transcription != ''
