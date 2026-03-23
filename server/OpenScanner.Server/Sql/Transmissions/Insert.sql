INSERT INTO transmissions (id, timestamp, frequency, alphaTag, description, lat, lon, alt, audio_path, duration, transcription, detectedTone, sourceID, targetID, speakerChain)
VALUES (@Id, @Timestamp, @Frequency, @AlphaTag, @Description, @Lat, @Lon, 0, @AudioPath, @Duration, @Transcription, @DetectedTone, @SourceID, @TargetID, @SpeakerChain)
