SELECT COALESCE(alphaTag, '') AS AlphaTag, frequency AS Frequency,
       COUNT(*) AS Count, COALESCE(SUM(duration), 0) AS AirtimeSeconds
FROM transmissions
WHERE timestamp >= @Since
GROUP BY COALESCE(alphaTag, ''), frequency
ORDER BY Count DESC, AirtimeSeconds DESC
LIMIT @Limit
