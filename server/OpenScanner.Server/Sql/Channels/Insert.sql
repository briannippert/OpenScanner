INSERT INTO channels (frequency, license, type, tone, alphaTag, description, mode, tag, lat, lon, range, avoid)
VALUES (@Frequency, @License, @Type, @Tone, @AlphaTag, @Description, @Mode, @Tag, @Lat, @Lon, @Range, @Avoid)
RETURNING id
