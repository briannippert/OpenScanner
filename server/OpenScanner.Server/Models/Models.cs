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

    /// <summary>
    /// If true, the channel will be skipped during scanning.
    /// </summary>
    public bool Avoid { get; set; }

    /// <summary>
    /// DMR time slot (1 or 2). Only applicable when Mode is "DMR".
    /// </summary>
    public int? DmrSlot { get; set; }

    /// <summary>
    /// DMR color code (0–15). Only applicable when Mode is "DMR".
    /// </summary>
    public int? DmrColorCode { get; set; }

    /// <summary>
    /// DMR talkgroup ID to monitor. Only applicable when Mode is "DMR".
    /// </summary>
    public int? DmrTalkgroup { get; set; }

    public Channel() { }

    public Channel(double frequency, string alphaTag, string description, string mode = "P25", string type = "RM", string tone = "", string tag = "", string license = "", bool avoid = false, int? dmrSlot = null, int? dmrColorCode = null, int? dmrTalkgroup = null)
    {
        Frequency = frequency;
        AlphaTag = alphaTag;
        Description = description;
        Mode = mode;
        Type = type;
        Tone = tone;
        Tag = tag;
        License = license;
        Avoid = avoid;
        DmrSlot = dmrSlot;
        DmrColorCode = dmrColorCode;
        DmrTalkgroup = dmrTalkgroup;
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
    /// Ordered chain of speaker IDs for the call, e.g. "12345 → 67890 → 12345".
    /// </summary>
    public string? SpeakerChain { get; set; }
    
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

    /// <summary>
    /// Whether this recording has been marked as a favorite.
    /// </summary>
    public bool IsFavorite { get; set; }

    public CallLog() { }

    public CallLog(string id, string timestamp, double frequency, string alphaTag, string description, double? lat, double? lon, string? audioPath, double? duration, string? transcription = null, int? sourceID = null, int? targetID = null, string? detectedTone = null, string? speakerChain = null)
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
        SpeakerChain = speakerChain;
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
/// A decoded signaling event surfaced to the event log — either a fire tone-out
/// (two-tone / Quick Call II) detection or an MDC1200 data packet.
/// </summary>
public class RadioEvent
{
    /// <summary>Unique identifier for the event.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Timestamp of the detection (ISO 8601).</summary>
    public string Timestamp { get; set; } = string.Empty;

    /// <summary>Event discriminator: "TONE_OUT", "MDC_PTT", or "MDC_EMERGENCY".</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Human-friendly label (tone set name, or MDC unit/op description).</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Frequency the event was detected on, in MHz.</summary>
    public double Frequency { get; set; }

    /// <summary>Channel alpha tag at the time of detection, if known.</summary>
    public string? AlphaTag { get; set; }

    /// <summary>Fire tone-out Tone A frequency in Hz (TONE_OUT only).</summary>
    public double? ToneA { get; set; }

    /// <summary>Fire tone-out Tone B frequency in Hz (TONE_OUT only).</summary>
    public double? ToneB { get; set; }

    /// <summary>MDC1200 unit (radio) ID (MDC_* only).</summary>
    public int? UnitId { get; set; }

    /// <summary>Id of the transmission recording this event coincided with, if any.</summary>
    public string? TransmissionId { get; set; }
}

/// <summary>
/// A decoded MDC1200 data packet.
/// </summary>
public class Mdc1200Packet
{
    /// <summary>16-bit unit (radio) ID.</summary>
    public int UnitId { get; set; }

    /// <summary>MDC opcode.</summary>
    public int Op { get; set; }

    /// <summary>MDC argument byte.</summary>
    public int Arg { get; set; }

    /// <summary>True when the packet represents an emergency.</summary>
    public bool IsEmergency { get; set; }

    /// <summary>Human-friendly description of the op/arg.</summary>
    public string Description { get; set; } = string.Empty;
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
/// Scanning mode for a frequency bank.
/// </summary>
public enum ScanMode
{
    /// <summary>
    /// All frequencies fit within 2.4 MHz window - scan continuously
    /// without frequency hopping for maximum responsiveness.
    /// </summary>
    FastScan,

    /// <summary>
    /// Frequencies exceed 2.4 MHz window - use dwell-based frequency
    /// hopping with 2-second dwell per 0.5 MHz cluster.
    /// </summary>
    FrequencyHop
}

/// <summary>
/// Represents a scanning bank with optimized mode selection.
/// </summary>
public class ScanBank
{
    /// <summary>
    /// Center frequency for this bank.
    /// </summary>
    public double CenterFrequency { get; set; }

    /// <summary>
    /// List of frequencies covered by this bank.
    /// </summary>
    public List<double> Frequencies { get; set; } = new();

    /// <summary>
    /// Total frequency spread in MHz (max - min).
    /// </summary>
    public double SpreadMHz { get; set; }

    /// <summary>
    /// Scan mode for this bank (FastScan or FrequencyHop).
    /// </summary>
    public ScanMode Mode { get; set; }

    /// <summary>
    /// Dwell time in milliseconds (for FrequencyHop mode).
    /// </summary>
    public int DwellTimeMs { get; set; } = 300;
}

/// <summary>
/// Per-channel state during parallel FastScan decoding.
/// </summary>
public record ParallelChannelState(
    Channel Channel,
    bool IsActive,
    double SignalStrength,
    bool IsRecording,
    int? SourceID = null,
    int? TargetID = null,
    string? SpeakerChain = null,
    string? CurrentTone = null
);

/// <summary>
/// A single point in the RF spectrum.
/// </summary>
public record SpectrumPoint(double Frequency, double Db);

/// <summary>
/// Snapshot of the transcription worker pool: how many clips are waiting and
/// how many workers are draining the queue.
/// </summary>
public record TranscriptionQueueStatus(int Queued, int Workers);

/// <summary>
/// Instantaneous system resource sample surfaced on the debug page. The client
/// polls this once per second to build the rolling CPU/memory graphs.
/// </summary>
public record SystemStats(
    double CpuPercent,
    double MemPercent,
    long MemUsedMb,
    long MemTotalMb,
    TranscriptionQueueStatus Transcription
);

/// <summary>State of a systemd unit (or the process itself when systemd is absent).</summary>
public record ServiceStatus(string Name, string State, string Detail);

/// <summary>A TCP socket the host is listening on.</summary>
public record ListeningPort(string Protocol, int Port, string Process);

/// <summary>Running services and open ports surfaced on the debug page.</summary>
public record ServicesSnapshot(List<ServiceStatus> Services, List<ListeningPort> Ports);

/// <summary>Compact SDR/scanner state for the debug page.</summary>
public record ScannerSummary(
    string Status,
    bool HardwareConnected,
    double? Frequency,
    double? SignalDb,
    double SignalStrength,
    double? Gain,
    double? Squelch,
    bool AudioStreaming
);

/// <summary>GPS link health: gpsd connectivity, fix age, and the last location.</summary>
public record GpsDiagnostics(bool GpsdConnected, double? SecondsSinceFix, GpsData? Location);

/// <summary>Aggregate recording/transcription counts from the database.</summary>
public record DbStats(int TotalRecordings, int Transcribed, int Pending, string? OldestUtc, string? NewestUtc);

/// <summary>Connected real-time WebSocket client counts.</summary>
public record ConnectionStats(int ControlClients, int AudioClients);

/// <summary>In-flight recording activity.</summary>
public record RecordingActivity(int ActiveCount, List<string> ActiveIds);

/// <summary>SDR reliability counters surfaced from the capture watchdog.</summary>
public record RadioDiagnostics(int RestartCount, double ThroughputKbps);

/// <summary>Status of the low-disk recording cleanup service.</summary>
public record CleanupStatus(string? LastRunUtc, long? LastFreeBytes, int TotalPurged);

/// <summary>
/// Composite, slower-changing diagnostics surfaced on the System Debug page
/// (polled less frequently than the per-second CPU/memory <see cref="SystemStats"/>).
/// </summary>
public record DiagnosticsSnapshot(
    string Uptime,
    ScannerSummary Scanner,
    GpsDiagnostics Gps,
    DbStats Database,
    ConnectionStats Connections,
    RecordingActivity Recording,
    RadioDiagnostics Radio,
    CleanupStatus Cleanup,
    string? TranscriptionModelStatus
);

/// <summary>
/// Storage usage statistics surfaced in the settings UI.
/// </summary>
public record StorageInfo(
    long RecordingsBytes,
    int RecordingsCount,
    long DatabaseBytes,
    long DiskFreeBytes,
    long DiskTotalBytes
);

/// <summary>
/// Real-time status of the scanner hardware and software.
/// </summary>
public record ScannerState(
    string Status, // "IDLE", "SCANNING", "RECEIVING", "MONITORING", "DEBUG"
    double SignalStrength,
    bool IsHardwareConnected = false,
    double? CurrentFrequency = null,
    Channel? CurrentChannel = null,
    double? CurrentSignalDb = null,
    bool IsAudioStreaming = false,
    double? Squelch = null,
    double? Gain = null,
    string? DeviceName = null,
    string? DevicePort = null,
    SpectrumPoint[]? RfSpectrum = null,
    GpsData? Gps = null,
    double? ManualHoldFrequency = null,
    string? LastTranscription = null,
    int? SourceID = null,
    int? TargetID = null,
    string? SpeakerChain = null,
    string? CurrentTone = null,
    string? LastDetectedTone = null,
    ParallelChannelState[]? ParallelChannels = null
);
        