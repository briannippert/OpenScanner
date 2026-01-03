namespace OpenScanner.Server.Models;

public record Channel(
    double Frequency,
    string AlphaTag,
    string Description,
    string Mode = "P25",
    string Type = "RM",
    string Tone = "",
    string Tag = "",
    string License = ""
)
{
    public int? Id { get; init; }
}

public record GpsData(
    double Lat,
    double Lon,
    double Alt,
    double Speed,
    string Time,
    int Fix,
    int Sats
);

public record SpectrumPoint(double Frequency, double Db);

public record ScannerState(
    string Status, // "IDLE", "SCANNING", "RECEIVING", "MONITORING"
    double SignalStrength,
    bool IsHardwareConnected = false,
    double? CurrentFrequency = null,
    Channel? CurrentChannel = null,
    double? CurrentSignalDb = null,
    bool IsAudioStreaming = false,
    double? Squelch = null,
    string? DeviceName = null,
    string? DevicePort = null,
    SpectrumPoint[]? RfSpectrum = null,
    GpsData? Gps = null
);

public record CallLog(
    string Id,
    string Timestamp,
    double Frequency,
    string AlphaTag,
    string Description,
    double? Lat = null,
    double? Lon = null,
    string? AudioPath = null,
    double? Duration = null
);
