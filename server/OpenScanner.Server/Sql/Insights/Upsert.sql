INSERT INTO transmission_insights (transmissionId, analyzedAt, isIncident, callType, service, summary, incidentStatus, address, placeName, crossStreet, city, state, matchedAddress, lat, lon)
VALUES (@TransmissionId, @AnalyzedAt, @IsIncident, @CallType, @Service, @Summary, @IncidentStatus, @Address, @PlaceName, @CrossStreet, @City, @State, @MatchedAddress, @Lat, @Lon)
ON CONFLICT(transmissionId) DO UPDATE SET
    analyzedAt = excluded.analyzedAt, isIncident = excluded.isIncident, callType = excluded.callType,
    service = excluded.service, summary = excluded.summary, incidentStatus = excluded.incidentStatus,
    address = excluded.address, placeName = excluded.placeName, crossStreet = excluded.crossStreet,
    city = excluded.city, state = excluded.state, matchedAddress = excluded.matchedAddress,
    lat = excluded.lat, lon = excluded.lon
