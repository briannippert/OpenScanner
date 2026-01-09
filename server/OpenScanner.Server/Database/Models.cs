using System.Text.Json.Serialization;

namespace OpenScanner.Server.Models;

/// <summary>
/// Represents a radio channel configuration.
/// </summary>
public class Channel
{
    /// <summary>
    /// Unique identifier for the channel.
    /// </summary>
    public int? Id { get; set; }

    /// <summary>
    /// Frequency in MHz.
    /// </summary>
    public double Frequency { get; set; }

    /// <summary>
    /// Short display name (Alpha Tag).
    /// </summary>
    public string AlphaTag { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of the channel.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Modulation mode (e.g., "P25", "FM", "NFM").
    /// </summary>
    public string Mode { get; set; } = "P25";

    /// <summary>
    /// Channel type (e.g., "RM" for Repeater/Mobile).
    /// </summary>
    public string Type { get; set; } = "RM";

    /// <summary>
    /// Squelch tone (CTCSS/DCS) or NAC code.
    /// </summary>
    public string Tone { get; set; } = string.Empty;

    /// <summary>
    /// Category tag (e.g., "Law Dispatch").
    /// </summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>
    /// FCC License callsign.
    /// </summary>
    public string License { get; set; } = string.Empty;

    /// <summary>
    /// Latitude of the transmitter.
    /// </summary>
    public double? Lat { get; set; }

    /// <summary>
    /// Longitude of the transmitter.
    /// </summary>
    public double? Lon { get; set; }

    /// <summary>
    /// Coverage range in miles.
    /// </summary>
    public double? Range { get; set; } // in miles

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

/// <summary>
/// Represents a recorded transmission log.
/// </summary>
public class CallLog
{
    /// <summary>
    /// Unique identifier for the log entry.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp of the recording start (ISO 8601).
    /// </summary>
    public string Timestamp { get; set; } = string.Empty;

    /// <summary>
    /// Frequency recorded in MHz.
    /// </summary>
    public double Frequency { get; set; }

    /// <summary>
    /// Channel name at the time of recording.
    /// </summary>
    public string AlphaTag { get; set; } = string.Empty;

    /// <summary>
    /// Channel description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Receiver latitude.
    /// </summary>
    public double? Lat { get; set; }

    /// <summary>
    /// Receiver longitude.
    /// </summary>
    public double? Lon { get; set; }

    /// <summary>
    /// Receiver altitude in feet.
    /// </summary>
    public double? Alt { get; set; }

    /// <summary>
    /// P25 Unit ID (Radio ID).
    /// </summary>
    public int? SourceID { get; set; }

    /// <summary>
    /// P25 Talkgroup ID.
    /// </summary>
    public int? TargetID { get; set; }
    
    /// <summary>
    /// Filename of the audio recording (relative to /audio).
    /// </summary>
    [JsonPropertyName("audio_path")]
    public string? AudioPath { get; set; }
    
    /// <summary>
    /// Duration of the recording in seconds.
    /// </summary>
    public double? Duration { get; set; }
    
    /// <summary>
    /// AI-generated text transcription.
    /// </summary>
    public string? Transcription { get; set; }

    /// <summary>
    /// Detected Fire Tone Out sequence (if any).
    /// </summary>
    public string? DetectedTone { get; set; }

    public CallLog() { }

    public CallLog(string id, string timestamp, double frequency, string alphaTag, string description, double? lat, double? lon, string? audioPath, double? duration, string? transcription = null, int? sourceID = null, int? targetID = null, string? detectedTone = null)
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
        SourceID = sourceID;
        TargetID = targetID;
        DetectedTone = detectedTone;
    }
}

/// <summary>
/// Configuration for a Fire Tone Out (2-tone paging) set.
/// </summary>
public class FireToneSet
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public int? Id { get; set; }

    /// <summary>
    /// Friendly name for the tone set (e.g., "Station 1").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Frequency of Tone A in Hz.
    /// </summary>
    public double FrequencyA { get; set; }

    /// <summary>
    /// Frequency of Tone B in Hz.
    /// </summary>
    public double FrequencyB { get; set; }

    /// <summary>
    /// Description of the unit or alert.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// GPS Telemetry data.
/// </summary>
public record GpsData(
    double Lat,
    double Lon,
    double Alt,
    double Speed,
    string Time,
    int Fix,
    int Sats,
    int? SatsVisible = null,
    double? Hdop = null
);

/// <summary>
/// A single point in the RF spectrum.
/// </summary>
public record SpectrumPoint(double Frequency, double Db);

/// <summary>
/// Real-time status of the scanner hardware and software.
/// </summary>
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
    double? ManualHoldFrequency = null,
    string? LastTranscription = null,
    int? SourceID = null,
    int? TargetID = null,
    string? CurrentTone = null,
    string? LastDetectedTone = null
);
        