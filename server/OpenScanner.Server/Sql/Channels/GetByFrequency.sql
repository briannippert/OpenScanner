-- The channel a transmission was heard on. Frequencies are unique; the tolerance absorbs
-- floating-point noise between the stored channel and the recorded transmission.
SELECT id, frequency, license, type, tone, alphaTag, description, mode, tag, lat, lon, range, avoid, dmrSlot, dmrColorCode, dmrTalkgroup, sdrDeviceId, city, state
FROM channels
WHERE ABS(frequency - @Frequency) < 0.0001
LIMIT 1
