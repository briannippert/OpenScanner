INSERT INTO transmissions (id, timestamp, frequency, alphaTag, description, lat, lon, alt, audio_path, duration, transcription, sourceID, targetID)
VALUES (@Id, @Timestamp, @Frequency, @AlphaTag, @Description, @Lat, @Lon, 0, @AudioPath, @Duration, @Transcription, @SourceID, @TargetID)
