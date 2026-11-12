INSERT INTO channels (frequency, license, type, tone, alphaTag, description, mode, tag, lat, lon, range, avoid, dmrSlot, dmrColorCode, dmrTalkgroup, sdrDeviceId)
VALUES (@Frequency, @License, @Type, @Tone, @AlphaTag, @Description, @Mode, @Tag, @Lat, @Lon, @Range, @Avoid, @DmrSlot, @DmrColorCode, @DmrTalkgroup, @SdrDeviceId)
RETURNING id
