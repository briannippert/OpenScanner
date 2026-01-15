UPDATE channels 
SET frequency=@Frequency, license=@License, type=@Type, tone=@Tone, 
    alphaTag=@AlphaTag, description=@Description, mode=@Mode, tag=@Tag,
    lat=@Lat, lon=@Lon, range=@Range, avoid=@Avoid
WHERE id=@Id
