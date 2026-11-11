SELECT COUNT(*) AS Transmissions,
       COALESCE(SUM(duration), 0) AS AirtimeSeconds,
       COALESCE(MAX(duration), 0) AS LongestSeconds,
       COALESCE(SUM(CASE WHEN transcription IS NOT NULL AND transcription <> '' THEN 1 ELSE 0 END), 0) AS Transcribed,
       COALESCE(SUM(CASE WHEN detectedTone IS NOT NULL AND detectedTone <> '' THEN 1 ELSE 0 END), 0) AS ToneOuts,
       COUNT(DISTINCT COALESCE(alphaTag, '') || '|' || frequency) AS ActiveChannels
FROM transmissions
WHERE timestamp >= @Since
