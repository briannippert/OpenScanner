using System.Text.Json.Serialization;

namespace OpenScanner.Server.Models;

public class Channel
{
    public int? Id { get; set; }
    public double Frequency { get; set; }
    public string AlphaTag { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Mode { get; set; } = "P25";
    public string Type { get; set; } = "RM";
    public string Tone { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;

    public Channel() { }

    public Channel(double frequency, string alphaTag, string description, string mode = "P25", string type = "RM", string tone = "", string tag = "", string license = "")
    {
        Frequency = frequency;
        AlphaTag = alphaTag;
        Description = description;
        Mode = mode;
        Type = type;
        Tone = tone;
        Tag = tag;
        License = license;
    }
}

public class CallLog
{
    public string Id { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public double Frequency { get; set; }
    public string AlphaTag { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public double? Alt { get; set; }
    
    [JsonPropertyName("audio_path")]
    public string? AudioPath { get; set; }
    
    public double? Duration { get; set; }
    
    public string? Transcription { get; set; }

    public CallLog() { }

    public CallLog(string id, string timestamp, double frequency, string alphaTag, string description, double? lat, double? lon, string? audioPath, double? duration, string? transcription = null)
    {
        Id = id;
        Timestamp = timestamp;
        Frequency = frequency;
        AlphaTag = alphaTag;
        Description = description;
        Lat = lat;
        Lon = lon;
        AudioPath = audioPath;
        Duration = duration;
        Transcription = transcription;
    }
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
    GpsData? Gps = null,
    double? ManualHoldFrequency = null
);