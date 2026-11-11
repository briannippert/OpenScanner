-- Transmissions per UTC hour since @Since. Timestamps are round-trip ("o") UTC
-- strings (2026-11-11T20:05:40.1250000Z), so the first 13 characters are the hour.
SELECT substr(timestamp, 1, 13) || ':00:00Z' AS Hour, COUNT(*) AS Count
FROM transmissions
WHERE timestamp >= @Since
GROUP BY substr(timestamp, 1, 13)
ORDER BY 1
