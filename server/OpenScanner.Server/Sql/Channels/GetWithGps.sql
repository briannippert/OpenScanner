SELECT id, frequency, license, type, tone, alphaTag, description, mode, tag, lat, lon, range, avoid FROM channels WHERE lat IS NOT NULL AND lon IS NOT NULL
