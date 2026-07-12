SELECT alphaTag, frequency, 'SRC' AS Kind, sourceID AS Value,
       COUNT(*) AS Count, MAX(timestamp) AS LastSeen
FROM transmissions
WHERE datetime(timestamp) >= datetime('now', @Window) AND sourceID IS NOT NULL
GROUP BY alphaTag, frequency, sourceID
UNION ALL
SELECT alphaTag, frequency, 'TG' AS Kind, targetID AS Value,
       COUNT(*) AS Count, MAX(timestamp) AS LastSeen
FROM transmissions
WHERE datetime(timestamp) >= datetime('now', @Window) AND targetID IS NOT NULL
GROUP BY alphaTag, frequency, targetID
ORDER BY alphaTag, frequency, Kind, Count DESC
