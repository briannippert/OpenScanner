SELECT detectedTone AS Name, COUNT(*) AS Count
FROM transmissions
WHERE timestamp >= @Since AND detectedTone IS NOT NULL AND detectedTone <> ''
GROUP BY detectedTone
ORDER BY Count DESC, Name
LIMIT @Limit
