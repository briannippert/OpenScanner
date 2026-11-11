SELECT COALESCE(alphaTag, '') AS AlphaTag, frequency AS Frequency, sourceID AS Id,
       COUNT(*) AS Count, COALESCE(SUM(duration), 0) AS AirtimeSeconds
FROM transmissions
WHERE timestamp >= @Since AND sourceID IS NOT NULL
GROUP BY COALESCE(alphaTag, ''), frequency, sourceID
ORDER BY Count DESC, AirtimeSeconds DESC
LIMIT @Limit
