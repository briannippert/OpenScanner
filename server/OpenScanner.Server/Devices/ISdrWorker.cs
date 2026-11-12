using OpenScanner.Server.Audio;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;

namespace OpenScanner.Server.Devices;

/// <summary>One SDR dongle's scanner: it scans, receives and records the channels assigned to it.</summary>
public interface ISdrWorker : IDisposable
{
    /// <summary>The dongle this worker drives (<see cref="SdrDeviceConfig.Id"/>).</summary>
    string DeviceId { get; }

    /// <summary>What this SDR currently carries, or null before the first assignment.</summary>
    DeviceAssignment? Assignment { get; }

    /// <summary>Raised whenever this SDR's state changes.</summary>
    event Action<ScannerState>? OnStateChanged;

    /// <summary>Raised when a signaling event (tone-out, MDC1200) is detected on this SDR's channels.</summary>
    event Action<RadioEvent>? OnNewEvent;

    /// <summary>This SDR's current state.</summary>
    ScannerState GetState();

    /// <summary>This SDR's capture reliability counters.</summary>
    RadioDiagnostics GetDiagnostics();

    /// <summary>
    /// Gives this SDR its channels and the librtlsdr index of its dongle. A running scan
    /// restarts to apply it.
    /// </summary>
    void Assign(DeviceAssignment assignment, int? deviceIndex);

    /// <summary>Starts scanning.</summary>
    void Start();

    /// <summary>Stops scanning and receiving.</summary>
    void Stop();

    /// <summary>Sets the squelch threshold, in dB of SNR.</summary>
    void SetSquelch(double db);

    /// <summary>Holds on a frequency.</summary>
    void HoldFrequency(double freq);

    /// <summary>Releases a hold or lock-on and resumes scanning.</summary>
    void ResumeScan();

    /// <summary>Temporarily skips a frequency.</summary>
    void AvoidFrequency(double freq, double durationSeconds);

    /// <summary>Takes this SDR off scanning to show a wideband spectrum.</summary>
    void StartDebugSpectrum(double freq, double? gain = null);

    /// <summary>Starts writing raw IQ to a file.</summary>
    void StartDumping(string label);

    /// <summary>Stops writing raw IQ.</summary>
    void StopDumping();

    /// <summary>The audio buffered ahead of the current transmission.</summary>
    byte[][] GetPreRollBuffer();
}

/// <summary>Creates SDR workers.</summary>
public interface ISdrWorkerFactory
{
    /// <summary>Creates a stopped worker for a dongle.</summary>
    ISdrWorker Create(string deviceId);
}

/// <summary>Creates <see cref="RtlDevice"/> workers.</summary>
public class RtlWorkerFactory : ISdrWorkerFactory
{
    private readonly IDatabase _db;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ToneDetector _toneDetector;
    private readonly IDecoderFactory _decoderFactory;
    private readonly IRecordingService _recordingService;
    private readonly IChannelService _channelService;
    private readonly AudioMixer _mixer;

    /// <summary>Initializes a new instance of the <see cref="RtlWorkerFactory"/> class.</summary>
    public RtlWorkerFactory(
        IDatabase db,
        ILoggerFactory loggerFactory,
        ToneDetector toneDetector,
        IDecoderFactory decoderFactory,
        IRecordingService recordingService,
        IChannelService channelService,
        AudioMixer mixer)
    {
        _db = db;
        _loggerFactory = loggerFactory;
        _toneDetector = toneDetector;
        _decoderFactory = decoderFactory;
        _recordingService = recordingService;
        _channelService = channelService;
        _mixer = mixer;
    }

    /// <inheritdoc />
    public ISdrWorker Create(string deviceId) => new RtlDevice(
        _db,
        _loggerFactory.CreateLogger<RtlDevice>(),
        _loggerFactory,
        _toneDetector,
        // Each worker decodes MDC1200 on the channel it is locked on to. One decoder shared
        // between SDRs would interleave their audio and attribute IDs to the wrong channel.
        new Mdc1200Decoder(_loggerFactory.CreateLogger<Mdc1200Decoder>()),
        _decoderFactory,
        _recordingService,
        _channelService,
        _mixer,
        deviceId);
}
