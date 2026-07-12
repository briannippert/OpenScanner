INSERT INTO aliases (kind, value, name, alphaTag, frequency)
VALUES (@Kind, @Value, @Name, @AlphaTag, @Frequency)
ON CONFLICT(kind, value, alphaTag, frequency) DO UPDATE SET name = excluded.name
RETURNING id
