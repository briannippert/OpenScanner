SELECT COALESCE(alphaTag, '') AS AlphaTag, frequency AS Frequency, targetID AS Id,
       COUNT(*) AS Count, COALESCE(SUM(duration), 0) AS AirtimeSeconds
FROM transmissions
WHERE timestamp >= @Since AND targetID IS NOT NULL
GROUP BY COALESCE(alphaTag, ''), frequency, targetID
ORDER BY Count DESC, AirtimeSeconds DESC
LIMIT @Limit
