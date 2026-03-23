SELECT id, frequency, license, type, tone, alphaTag, description, mode, tag, lat, lon, range, avoid, dmrSlot, dmrColorCode, dmrTalkgroup FROM channels WHERE lat IS NOT NULL AND lon IS NOT NULL
