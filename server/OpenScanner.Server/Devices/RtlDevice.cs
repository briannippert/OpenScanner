using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using OpenScanner.Server.Audio;
using OpenScanner.Server.DSP;
using OpenScanner.Server.Models;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Services;

namespace OpenScanner.Server.Devices;

/// <summary>
/// Drives one RTL-SDR dongle: scans the channels assigned to it, either all at once
/// (FastScan) or by hopping between them, and receives and records what it hears.
/// <see cref="RadioCoordinator"/> runs one per dongle and combines them into the radio
/// the rest of the app sees.
/// </summary>
public class RtlDevice : ISdrWorker
{
    private readonly IDatabase _db;
    private readonly ILogger<RtlDevice> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ToneDetector _toneDetector;
    private readonly Mdc1200Decoder _mdc;
    private readonly IDecoderFactory _decoderFactory;
    private readonly IRecordingService _recordingService;
    private readonly IChannelService _channelService;

    /// <inheritdoc />
    public event Action<ScannerState>? OnStateChanged;
    
    /// <inheritdoc />
    public event Action<RadioEvent>? OnNewEvent;

    private ScannerState _state = new ScannerState("IDLE", 0);
    private bool _manualOverride = false;

    // Capture-watchdog diagnostics surfaced on the debug page.
    private int _restartCount;
    private long _totalBytesRead;
    private (DateTime When, long Bytes)? _lastThroughputSample;
    
    private string? _lastDetectedTone;
    // Normally 2s of silence ends a transmission. After a fire tone-out we use a
    // longer inactivity timeout so the pause between the tone ending and the
    // dispatch voice keying up doesn't split it into a separate transmission —
    // the tone and the voice that follows stay one recording.
    private const int ActivityTimeoutMs = 2000;
    private const int ToneHoldMs = 6000;

    /// <summary>
    /// Default squelch, in dB of SNR above the measured noise floor. Squelch used to be an
    /// absolute dB level, which meant it silently changed meaning with the gain setting and
    /// with the FFT size. It is not persisted, so there is no stored value on the old scale
    /// to migrate.
    /// </summary>
    private const double DefaultSquelchSnrDb = 10.0;

    private DateTime _recordingLockoutUntil = DateTime.MinValue;
    private CancellationTokenSource? _scanCts;
    private Process? _scannerProcess;
    private Dictionary<double, int> _channelHits = new();
    private DateTime _scanStartTime;
    private CancellationTokenSource? _sessionTimeoutCts;
    private CancellationTokenSource? _decodeCts;
    private IDecoder? _currentDecoder;
    private CancellationTokenSource? _activityTimeoutCts;
    private DateTime _lastActivityReset = DateTime.MinValue;

    private FileStream? _iqDumpStream;

    // The channel whose decoder audio is open in the mixer while locked on.
    private double? _lockedAudioFrequency;
    private string? _iqDumpPath;
    
    // Audio buffering
    private readonly LinkedList<byte[]> _preRollBuffer = new();
    private const int PreRollMaxBytes = 96000 * 5; // ~5 seconds at 48kHz 16-bit to capture full transmission start

    private readonly ConcurrentDictionary<double, DateTime> _channelLockouts = new();

    // Parallel FastScan mode
    private List<ChannelPipeline>? _parallelPipelines;
    private readonly ConcurrentDictionary<double, CancellationTokenSource> _parallelActivityTimeouts = new();
    // Per-channel throttle for the audio-driven activity-timeout reset (mirrors
    // the single-channel _lastActivityReset throttle) to avoid task churn.
    private readonly ConcurrentDictionary<double, DateTime> _parallelLastAudioReset = new();
    private readonly ConcurrentDictionary<double, string?> _parallelLastTones = new();
    // Live audio goes to the mixer shared by every SDR, which sums all open channels into
    // one panned stereo stream. Without it, simultaneous channels would broadcast independent
    // chunks that the browser scheduler serializes end-to-end (garbled overlapping audio).
    private readonly AudioMixer _mixer;

    // What this SDR carries. Null until the coordinator assigns it, in which case the
    // worker scans every non-avoided channel (single-SDR behaviour, and what tests use).
    private DeviceAssignment? _assignment;
    private bool _running;

    /// <summary>
    /// Initializes a new instance of the <see cref="RtlDevice"/> class.
    /// </summary>
    /// <param name="db">Database, for persisting signaling events.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="loggerFactory">Logger factory, for per-channel pipelines.</param>
    /// <param name="toneDetector">The shared fire-tone detector; this worker uses its own keyed streams.</param>
    /// <param name="mdc">This worker's own MDC1200 decoder, for the channel it is locked on to.</param>
    /// <param name="decoderFactory">Creates voice decoders.</param>
    /// <param name="recordingService">Records transmissions.</param>
    /// <param name="channelService">The channel list, for manual holds and unassigned operation.</param>
    /// <param name="mixer">The audio mixer shared by every SDR.</param>
    /// <param name="deviceId">The dongle this worker drives (<see cref="SdrDeviceConfig.Id"/>).</param>
    public RtlDevice(
        IDatabase db,
        ILogger<RtlDevice> logger,
        ILoggerFactory loggerFactory,
        ToneDetector toneDetector,
        Mdc1200Decoder mdc,
        IDecoderFactory decoderFactory,
        IRecordingService recordingService,
        IChannelService channelService,
        AudioMixer mixer,
        string deviceId = SdrManager.DefaultDeviceId)
    {
        _db = db;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _toneDetector = toneDetector;
        _mdc = mdc;
        _decoderFactory = decoderFactory;
        _recordingService = recordingService;
        _channelService = channelService;
        _mixer = mixer;
        DeviceId = deviceId;
        _state = new ScannerState("IDLE", 0);

        // Without an assignment (one SDR, nothing planned yet) the persisted legacy tuning
        // applies. Gain: dB, or 0/absent = AUTO (tuner AGC). Ppm: 0/absent = uncorrected.
        _state = _state with
        {
            Gain = ReadNumericSetting("SdrGain"),
            Ppm = ReadNumericSetting("SdrPpm"),
        };

        // The tone detector is shared by every SDR. Each worker feeds it keyed streams (one
        // per channel) and reacts only to detections on channels it is carrying.
        _toneDetector.OnStreamToneDetected += HandleStreamTone;

        _mdc.OnPacket += (pkt) => {
            // Treat the MDC1200 unit ID like a P25 radio ID: attribute it to the current
            // transmission via the normal activity path so it lands on the recording's
            // SourceID, rather than logging a separate signaling event.
            if (_state.CurrentChannel != null)
                HandleActivity(_state.CurrentChannel, src: pkt.UnitId, tone: pkt.IsEmergency ? "EMRG" : null);
        };
    }

    /// <inheritdoc />
    public string DeviceId { get; }

    /// <inheritdoc />
    public DeviceAssignment? Assignment => _assignment;

    /// <summary>
    /// The channels this SDR scans: its assignment, or every non-avoided channel when it has none.
    /// </summary>
    private List<Channel> ScanChannels =>
        _assignment != null
            ? _assignment.Channels.ToList()
            : _channelService.Channels.Where(c => !c.Avoid).ToList();

    /// <inheritdoc />
    public void Assign(DeviceAssignment assignment, int? deviceIndex)
    {
        _assignment = assignment;
        _deviceIndex = deviceIndex;
        UpdateState(_state with
        {
            Gain = assignment.GainFor(),
            Ppm = assignment.Device.Ppm,
        });

        // Gain, ppm, device and channels are all baked into the capture at launch, so a
        // running scan restarts to pick the new assignment up. A manual hold or debug sweep
        // is left alone; the assignment applies when it ends.
        if (_running && !_manualOverride && _state.Status != "DEBUG")
        {
            StopScanning();
            StopDecoding();
            UpdateState(_state with
            {
                Status = "IDLE", CurrentFrequency = null, CurrentChannel = null,
                SignalStrength = 0, ManualHoldFrequency = null, ParallelChannels = null,
            });
            Task.Delay(250).ContinueWith(_ => StartScanning());
        }
    }

    /// <summary>
    /// A fire tone was detected on one of the shared detector's keyed streams. Act on it only
    /// if the channel is this SDR's: one of its parallel channels, or the one it is locked to.
    /// </summary>
    private void HandleStreamTone(FireToneSet tone, double freq)
    {
        var parallel = _parallelPipelines?.FirstOrDefault(p => p.Channel.Frequency == freq)?.Channel;
        if (parallel != null)
        {
            // Same as a tone heard on a parallel channel's decoder: record it with the tone
            // label and keep the clip open for the dispatch voice that follows.
            HandleParallelActivity(parallel, null, null, tone.Name);
            RaiseRadioEvent(new RadioEvent
            {
                Type = "TONE_OUT",
                Label = tone.Name,
                ToneA = tone.FrequencyA,
                ToneB = tone.FrequencyB
            }, parallel);
            return;
        }

        var locked = _state.CurrentChannel;
        if (_currentDecoder == null || locked == null || Math.Abs(locked.Frequency - freq) > 0.0001) return;

        _lastDetectedTone = tone.Name;
        // Extend the current recording's stop timer now so that if the tone is the last
        // audio before the carrier drops, we still wait the full hold (measured from the tone
        // ending) for the dispatch voice to follow.
        if (IsRecordingLockedChannel) ScheduleStopTimeout();
        UpdateState(_state with { LastDetectedTone = tone.Name });
        RaiseRadioEvent(new RadioEvent
        {
            Type = "TONE_OUT",
            Label = tone.Name,
            ToneA = tone.FrequencyA,
            ToneB = tone.FrequencyB
        }, locked);
    }

    /// <summary>Whether the channel this SDR is locked on to is being recorded.</summary>
    private bool IsRecordingLockedChannel =>
        _state.CurrentChannel is { } ch && _recordingService.IsChannelRecording(ch.Frequency);

    /// <inheritdoc />
    public void Dispose()
    {
        _toneDetector.OnStreamToneDetected -= HandleStreamTone;
        Stop();
    }

    /// <inheritdoc />
    public ScannerState GetState() => _state;

    /// <inheritdoc />
    public RadioDiagnostics GetDiagnostics()
    {
        // Throughput = bytes read since the previous call, over elapsed wall time.
        var now = DateTime.UtcNow;
        var bytes = System.Threading.Interlocked.Read(ref _totalBytesRead);
        double kbPerSec = 0;
        if (_lastThroughputSample is { } prev)
        {
            var elapsed = (now - prev.When).TotalSeconds;
            if (elapsed > 0) kbPerSec = (bytes - prev.Bytes) / 1024.0 / elapsed;
        }
        _lastThroughputSample = (now, bytes);
        return new RadioDiagnostics(_restartCount, Math.Round(Math.Max(0, kbPerSec), 1));
    }

    /// <inheritdoc />
    public void SetSquelch(double db)
    {
        UpdateState(_state with { Squelch = db });
        _logger.LogInformation($"Squelch set to {db}dB SNR");
    }

    private double? ReadNumericSetting(string key)
    {
        var raw = _db.GetSettingAsync(key).GetAwaiter().GetResult();
        return double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>Formats a number for a command-line argument, immune to the host locale.</summary>
    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// The rtl_sdr/rtl_fm tuning arguments shared by every capture: tuner gain and crystal
    /// error correction. Both tools spell these the same way and both treat "-g 0" as AUTO.
    /// </summary>
    private string TuningArgs(Channel? channel = null) => CurrentTuning(channel).CaptureArgs();

    /// <summary>
    /// The front-end settings for a capture, also handed to a decoder's own rtl_fm. A hopping
    /// SDR captures one channel at a time, so it can use that channel's own gain class;
    /// a FastScan SDR uses the gain for its whole mix.
    /// </summary>
    private SdrTuning CurrentTuning(Channel? channel = null) =>
        _assignment != null
            ? new(_assignment.GainFor(channel), _assignment.Device.Ppm, _deviceIndex)
            : new(_state.Gain ?? 0, _state.Ppm ?? 0, _deviceIndex);

    /// <summary>
    /// The librtlsdr index of the dongle this device drives, or null for the default (index 0).
    /// Single-device installs leave it null, which keeps the command lines unchanged.
    /// Takes effect on the next capture launch.
    /// </summary>
    internal int? DeviceIndex { get => _deviceIndex; set => _deviceIndex = value; }
    private int? _deviceIndex;

    /// <inheritdoc />
    public void Start()
    {
        _running = true;
        if (_state.Status != "IDLE") return;
        _manualOverride = false;
        _logger.LogInformation($"Starting scanner on SDR {DeviceId}...");
        StartScanning();
    }

    /// <inheritdoc />
    public void Stop()
    {
        _running = false;
        StopScanning();
        StopDecoding();
        UpdateState(_state with { 
            Status = "IDLE", 
            CurrentFrequency = null, 
            CurrentChannel = null, 
            SignalStrength = 0,
            ManualHoldFrequency = null,
            ParallelChannels = null
        });
    }

    /// <inheritdoc />
    public void HoldFrequency(double freq)
    {
        var channel = _channelService.Channels.FirstOrDefault(c => Math.Abs(c.Frequency - freq) < 0.001);
        if (channel == null) return;

        _logger.LogInformation($"Manual hold on {channel.AlphaTag}");
        _manualOverride = true;
        
        StopScanning();
        StopDecoding();

        _recordingLockoutUntil = DateTime.UtcNow.AddSeconds(3);
        UpdateState(_state with { ManualHoldFrequency = freq });
        LockOn(channel);
    }

    /// <inheritdoc />
    public void StartDebugSpectrum(double freq, double? gain = null)
    {
        var targetGain = gain ?? 40; // Default to 40dB for debug
        var gainStr = targetGain == 0 ? "AUTO" : $"{targetGain} dB";
        _logger.LogInformation($"Starting RF Spectrum Debug at {freq} MHz (Gain: {gainStr})");
        _manualOverride = true;

        StopScanning();
        StopDecoding();

        // Deliberately does not touch _state.Gain: that field is the *configured* tuner gain
        // and drives every capture's -g. The debug sweep's gain is a transient argument.
        UpdateState(_state with { Status = "DEBUG", CurrentFrequency = freq, ManualHoldFrequency = freq, RfSpectrum = null });

        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        Task.Run(async () => {
            try 
            {
                await RunDebugSpectrumLoop(freq, targetGain, token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Debug Spectrum Loop");
                UpdateState(_state with { Status = "IDLE" });
            }
        }, token);
    }

    private async Task RunDebugSpectrumLoop(double freq, double gain, CancellationToken token)
    {
        await Task.Delay(250, token);

        int sampleRate = 2400000; 
        var binPath = PlatformTools.RtlSdr;
        // Debug spectrum takes an explicit gain (it sweeps to find one), but still needs the
        // configured ppm or the displayed spectrum sits offset from the real frequencies.
        var args = $"-f {freq}M -s {sampleRate} {(CurrentTuning() with { GainDb = gain }).CaptureArgs()} -b 1 -";

        _logger.LogInformation($"Debug Spectrum HW: {binPath} {args}");

        var psi = new ProcessStartInfo(binPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try 
        {
            _scannerProcess = Process.Start(psi);
            UpdateState(_state with { IsHardwareConnected = true });
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to start rtl_sdr debug process");
             UpdateState(_state with { IsHardwareConnected = false });
             return;
        }

        if (_scannerProcess == null) return;

        // Monitor Stderr
        _ = Task.Run(async () => {
            try 
            {
                while (!token.IsCancellationRequested && !_scannerProcess.HasExited)
                {
                    var line = await _scannerProcess.StandardError.ReadLineAsync(token);
                    if (string.IsNullOrEmpty(line)) continue;
                    _logger.LogInformation($"Debug HW: {line}");
                }
            } 
            catch (OperationCanceledException) { }
        }, token);

        var bufferSize = 16384 * 4;
        var buffer = new byte[bufferSize];
        var baseStream = _scannerProcess.StandardOutput.BaseStream;
        var lastUpdate = DateTime.MinValue;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var bytesRead = await baseStream.ReadAsync(buffer, 0, buffer.Length, token);
                if (bytesRead <= 0) break;

                if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds < 30) continue;
                lastUpdate = DateTime.UtcNow;

                ProcessSamplesForSpectrum(buffer, bytesRead, freq, sampleRate, 1024);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { _scannerProcess?.Kill(true); } catch { }
            _scannerProcess = null;
        }
    }

    /// <inheritdoc />
    public void AvoidFrequency(double freq, double durationSeconds)
    {
        _channelLockouts[freq] = DateTime.UtcNow.AddSeconds(durationSeconds);
        _logger.LogInformation($"Temporarily avoiding {freq} MHz for {durationSeconds} seconds.");

        // If we are currently on this frequency, resume scan
        if (_state.CurrentFrequency.HasValue && Math.Abs(_state.CurrentFrequency.Value - freq) < 0.001)
        {
            ResumeScan();
        }
    }

    /// <inheritdoc />
    public void ResumeScan()
    {
        _logger.LogInformation("Resume/Skip requested.");

        // Force reset regardless of current state
        _manualOverride = false;
        
        StopDecoding(); // Stops the decoder
        StopScanning(); // Stops the rtl_sdr process and the scanner loop

        _recordingLockoutUntil = DateTime.UtcNow.AddSeconds(3);
        
        // Ensure state is IDLE so StartScanning accepts the request
        UpdateState(_state with { 
            Status = "IDLE", 
            CurrentFrequency = null, 
            CurrentChannel = null, 
            SignalStrength = 0,
            ManualHoldFrequency = null,
            ParallelChannels = null
        });
        
        // Small delay to let hardware settle before restarting the scan
        Task.Delay(250).ContinueWith(_ => StartScanning());
    }

    /// <inheritdoc />
    public void StartDumping(string label)
    {
        var dataDir = AppPaths.SamplesDir;
        if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);

        // Sanitize the user-supplied label to a safe filename token: this strips path
        // separators and "." so it cannot traverse directories (path injection) and
        // removes control characters/newlines that could forge log entries.
        var safeLabel = System.Text.RegularExpressions.Regex.Replace(label ?? "", "[^A-Za-z0-9_-]", "_");
        if (safeLabel.Length == 0) safeLabel = "dump";

        var fileName = Path.GetFileName($"{safeLabel}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.iq");
        var fullPath = Path.GetFullPath(Path.Combine(dataDir, fileName));

        // Canonicalize and verify the resolved path stays inside the samples directory,
        // so a crafted label can never escape it (path-injection barrier).
        if (!fullPath.StartsWith(dataDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid IQ dump path");

        _iqDumpPath = fullPath;
        _iqDumpStream = new FileStream(fullPath, FileMode.Create);
        _logger.LogInformation("Started IQ dumping to {Path}", _iqDumpPath);
    }

    /// <inheritdoc />
    public void StopDumping()
    {
        if (_iqDumpStream != null)
        {
            _iqDumpStream.Close();
            _iqDumpStream = null;
            _logger.LogInformation($"Stopped IQ dumping to {_iqDumpPath}");
            _iqDumpPath = null;
        }
    }

    /// <summary>
    /// Public test helper to expose CalculateScanBanks for unit testing.
    /// </summary>
    public List<ScanBank> CalculateScanBanksForTesting(List<Channel> channels)
    {
        return CalculateScanBanks(channels);
    }

    /// <inheritdoc />
    public byte[][] GetPreRollBuffer()
    {
        lock (_preRollBuffer)
        {
            return _preRollBuffer.ToArray();
        }
    }

    private void UpdateState(ScannerState newState)
    {
        _state = newState;
        OnStateChanged?.Invoke(_state);
    }

    /// <summary>
    /// Stamps the current channel context onto a detected signaling event, persists it,
    /// and broadcasts it to clients. Fire-and-forget persistence mirrors the recording path.
    /// </summary>
    private void RaiseRadioEvent(RadioEvent e, Channel? channel = null)
    {
        e.Id = Guid.NewGuid().ToString();
        e.Timestamp = DateTime.UtcNow.ToString("o");
        e.Frequency = channel?.Frequency ?? _state.CurrentChannel?.Frequency ?? _state.CurrentFrequency ?? 0;
        e.AlphaTag = channel?.AlphaTag ?? _state.CurrentChannel?.AlphaTag;
        // Link the event to the recording it coincides with (if any) so the UI can
        // jump to it in the transmission history.
        e.TransmissionId ??= _recordingService.CurrentRecordingId;

        _db.AddRadioEventAsync(e).ContinueWith(
            t => _logger.LogError(t.Exception, "Failed to persist radio event"),
            TaskContinuationOptions.OnlyOnFaulted);

        OnNewEvent?.Invoke(e);
    }

    private void StopScanning()
    {
        _scanCts?.Cancel();
        try { _scannerProcess?.Kill(true); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to kill scanner process"); }
        _scannerProcess = null;

        // Clean up parallel pipelines
        StopParallelPipelines();
    }

    private void StopParallelPipelines()
    {
        if (_parallelPipelines != null)
        {
            foreach (var pipeline in _parallelPipelines)
            {
                // Close the channel in the shared mixer first so no tick mixes a dead channel,
                // and drop only this channel's tone state: other SDRs are still detecting.
                _mixer.Close(pipeline.Channel.Frequency);
                _toneDetector.ResetStream(pipeline.Channel.Frequency);
                try { pipeline.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing channel pipeline"); }
            }
            _parallelPipelines = null;
        }

        // Cancel all per-channel activity timeouts
        foreach (var kvp in _parallelActivityTimeouts)
        {
            kvp.Value.Cancel();
        }
        _parallelActivityTimeouts.Clear();
        _parallelLastAudioReset.Clear();
        _parallelLastTones.Clear();
    }

    private void StartScanning()
    {
        var activeChannels = ScanChannels;
        if (activeChannels.Count == 0 || _manualOverride || _state.Status == "RECEIVING") return;

        _channelHits.Clear();
        _scanStartTime = DateTime.UtcNow;
        UpdateState(_state with { Status = "SCANNING", CurrentFrequency = null, CurrentChannel = null, SignalStrength = 0, ManualHoldFrequency = null, ParallelChannels = null });

        var banks = CalculateScanBanks(activeChannels);

        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        // Use parallel mode for single-bank FastScan with multiple channels
        if (banks.Count == 1 && banks[0].Mode == ScanMode.FastScan && banks[0].Frequencies.Count > 0)
        {
            Task.Run(() => RunParallelScan(banks[0], activeChannels, token), token);
        }
        else
        {
            var rate = 2048000;
            Task.Run(() => RunScannerLoop(banks, rate, token), token);
        }
    }

    /// <summary>
    /// Main scanner loop that cycles through scan banks.
    /// If multiple banks exist, each bank runs with a dwell timer before moving to the next.
    /// If only one bank, it runs continuously until the token is cancelled.
    /// </summary>
    private async Task RunScannerLoop(List<ScanBank> banks, int sampleRate, CancellationToken token)
    {
        int bankIndex = 0;
        int failures = 0;

        while (!token.IsCancellationRequested)
        {
            if (banks.Count == 0) break;

            var bank = banks[bankIndex];

            // Broadcast the current scan bank so the frontend shows real-time channel hopping.
            // For multi-channel FastScan banks, don't show the RTL-SDR center frequency — it's
            // not in the user's channel list. Show null so the frontend can indicate "watching N channels".
            var bankDisplayFreq = bank.Frequencies.Count == 1 ? bank.Frequencies[0] : (double?)null;
            var bankDisplayChannel = bank.Frequencies.Count == 1
                ? ScanChannels.FirstOrDefault(c => Math.Abs(c.Frequency - bank.Frequencies[0]) < 0.001)
                : null;
            UpdateState(_state with { CurrentFrequency = bankDisplayFreq, CurrentChannel = bankDisplayChannel });

            long bytes = 0;
            if (banks.Count > 1)
            {
                // Multiple banks: dwell on each before hopping to the next
                using var dwellCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                dwellCts.CancelAfter(bank.DwellTimeMs);

                try
                {
                    bytes = await RunSingleScanSegment(bank.CenterFrequency, sampleRate, dwellCts.Token, bankDisplayChannel);
                }
                catch (OperationCanceledException)
                {
                    // Expected when dwell time expires or main token cancelled
                }
            }
            else
            {
                // Single bank: run continuously until signal lock or manual stop
                try
                {
                    bytes = await RunSingleScanSegment(bank.CenterFrequency, sampleRate, token, bankDisplayChannel);
                }
                catch (OperationCanceledException) { }
            }

            if (token.IsCancellationRequested) break;

            // No data at all means rtl_sdr couldn't reach the dongle (e.g. it dropped off
            // USB). Back off instead of relaunching it in a tight loop, and keep retrying.
            if (bytes > 0)
            {
                failures = 0;
            }
            else
            {
                failures++;
                var delay = ReconnectDelay(failures);
                _logger.LogWarning($"SDR delivered no data; reconnecting in {delay.TotalSeconds:F0}s (attempt {failures + 1})");
                System.Threading.Interlocked.Increment(ref _restartCount);
                try { await Task.Delay(delay, token); }
                catch (OperationCanceledException) { break; }
            }
            
            bankIndex = (bankIndex + 1) % banks.Count;
        }

        // Loop exited
        if (!token.IsCancellationRequested && _state.Status == "SCANNING")
        {
            UpdateState(_state with { Status = "IDLE" });
        }
    }

    /// <summary>
    /// Parallel FastScan: runs all channel decoders simultaneously using a single rtl_sdr
    /// wideband capture. Each channel is isolated via a C# DSP channelizer and fed to its
    /// own decoder. Activity is detected by actual decoder output, never missing transmission starts.
    /// </summary>
    private async Task RunParallelScan(ScanBank bank, List<Channel> channels, CancellationToken token)
    {
        // Select a sample rate that covers all channels and gives integer decimation to 48 kHz
        double spreadHz = bank.SpreadMHz * 1e6;
        int sampleRate = Channelizer.SelectSampleRate(spreadHz);
        int outputRate = 48000;

        _logger.LogInformation(
            $"Parallel FastScan: {channels.Count} channels, spread {bank.SpreadMHz:F2} MHz, " +
            $"sample rate {sampleRate / 1e6:F3} MHz, center {bank.CenterFrequency:F3} MHz");

        // Create a ChannelPipeline for each active channel
        var pipelines = new List<ChannelPipeline>();
        foreach (var ch in channels)
        {
            if (ch.Avoid) continue;

            var pipeline = new ChannelPipeline(
                ch, sampleRate, outputRate, bank.CenterFrequency,
                _decoderFactory, _logger, _loggerFactory, _state.Squelch ?? DefaultSquelchSnrDb);

            // Wire up events
            pipeline.OnAudio += HandleParallelAudio;
            pipeline.OnActivity += HandleParallelActivity;
            pipeline.OnActivityEnded += HandleParallelActivityEnded;
            pipeline.OnMetadata += (channel, line) => _logger.LogInformation($"[{channel.AlphaTag}] {line}");

            pipeline.Start(token);
            pipelines.Add(pipeline);
        }
        _parallelPipelines = pipelines;

        // Open each channel in the shared stereo mixer. Pan positions come from the plan-wide
        // layout the coordinator sets, so channels on different SDRs share one stereo field.
        foreach (var pipeline in pipelines)
        {
            _mixer.Open(pipeline.Channel.Frequency);
            var (left, right) = _mixer.PanFor(pipeline.Channel.Frequency);
            _logger.LogInformation(
                $"Parallel pan: {pipeline.Channel.AlphaTag} ({pipeline.Channel.Frequency} MHz) -> L={left:F2} R={right:F2}");
        }

        // Build initial parallel channel states for the UI
        UpdateParallelState();

        // Keep the capture alive. If rtl_sdr exits or stalls (the dongle dropped off USB,
        // say), relaunch it with backoff instead of leaving the scanner silently idle.
        // The channel pipelines are kept across relaunches.
        int failures = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(250, token); // USB settle time
                long bytes = await RunParallelCapture(bank, sampleRate, token);
                if (token.IsCancellationRequested) break;

                // A capture that delivered data was a working device that later dropped:
                // retry promptly. Back off only while the device keeps failing to come up.
                failures = bytes > 0 ? 0 : failures + 1;
                var delay = ReconnectDelay(failures);
                UpdateState(_state with { IsHardwareConnected = false });
                _logger.LogWarning($"SDR capture ended after {bytes} bytes; reconnecting in {delay.TotalSeconds:F0}s (attempt {failures + 1})");
                System.Threading.Interlocked.Increment(ref _restartCount);
                await Task.Delay(delay, token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // A new scan may already have installed its own pipelines; only tear down ours.
            if (ReferenceEquals(_parallelPipelines, pipelines))
                StopParallelPipelines();
        }
    }

    /// <summary>
    /// Backoff between SDR reconnect attempts: 1s, 2s, 4s ... capped at 30s.
    /// </summary>
    internal static TimeSpan ReconnectDelay(int consecutiveFailures) =>
        TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(consecutiveFailures, 5)));

    /// <summary>
    /// Runs one rtl_sdr wideband capture for parallel scan, feeding every channel pipeline
    /// until the process exits, stalls, or the token is cancelled. Returns the bytes read.
    /// </summary>
    private async Task<long> RunParallelCapture(ScanBank bank, int sampleRate, CancellationToken token)
    {
        var binPath = PlatformTools.RtlSdr;
        var args = $"-f {bank.CenterFrequency:F3}M -s {sampleRate} {TuningArgs()} -b 1 -";
        _logger.LogInformation($"Parallel Scanner: {binPath} {args}");

        var psi = new ProcessStartInfo(binPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start rtl_sdr for parallel scan");
            return 0;
        }

        if (process == null) return 0;
        _scannerProcess = process;

        // Monitor stderr for hardware messages. A missing device makes rtl_sdr exit on its
        // own; the caller's reconnect loop handles that, so don't cancel the scan here.
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested && !process.HasExited)
                {
                    var line = await process.StandardError.ReadLineAsync(token);
                    if (string.IsNullOrEmpty(line)) continue;
                    _logger.LogInformation($"Scanner HW: {line}");

                    if (line.Contains("No supported devices") || line.Contains("Failed to open"))
                        UpdateState(_state with { IsHardwareConnected = false });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogDebug(ex, "Error monitoring scanner stderr"); }
        }, token);

        // Main IQ distribution loop
        var bufferSize = 16384 * 4;
        var buffer = new byte[bufferSize];
        var baseStream = process.StandardOutput.BaseStream;
        var lastFftUpdate = DateTime.MinValue;
        var lastMeterUpdate = DateTime.MinValue;
        var segmentStartTime = DateTime.UtcNow;

        // FFT parameters for spectrum display (reuse existing logic)
        int fftScanRate = sampleRate; // Use actual sample rate for FFT calculations

        long totalBytesRead = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Watchdog
                using var watchdogCts = new CancellationTokenSource();
                var readTask = baseStream.ReadAsync(buffer, 0, buffer.Length, token);
                var timeoutTask = Task.Delay(5000, watchdogCts.Token);

                var completedTask = await Task.WhenAny(readTask, timeoutTask);
                if (completedTask == timeoutTask)
                {
                    _logger.LogWarning($"Parallel scanner stalled. Process alive: {!process.HasExited}.");
                    break;
                }

                watchdogCts.Cancel();
                var bytesRead = await readTask;
                if (bytesRead <= 0) break;

                if (totalBytesRead == 0)
                    UpdateState(_state with { IsHardwareConnected = true });
                totalBytesRead += bytesRead;
                System.Threading.Interlocked.Add(ref _totalBytesRead, bytesRead);

                // Warm-up: skip first 50ms
                if ((DateTime.UtcNow - segmentStartTime).TotalMilliseconds < 50) continue;

                // IQ dump support
                if (_iqDumpStream != null)
                    await _iqDumpStream.WriteAsync(buffer, 0, bytesRead, token);

                // Distribute IQ to ALL channel pipelines (never skip - audio data must be continuous)
                var pipelines = _parallelPipelines;
                if (pipelines != null)
                {
                    DistributeIQ(pipelines, buffer, bytesRead);
                }

                // Rate-limited FFT for spectrum display (~50 Hz)
                if ((DateTime.UtcNow - lastFftUpdate).TotalMilliseconds >= 20)
                {
                    lastFftUpdate = DateTime.UtcNow;
                    try
                    {
                        ProcessSamplesForSpectrum(buffer, bytesRead, bank.CenterFrequency, fftScanRate);
                    }
                    catch (Exception ex) { _logger.LogError(ex, "Error processing spectrum"); }
                }

                // Rate-limited signal meter update (~5 Hz)
                if ((DateTime.UtcNow - lastMeterUpdate).TotalMilliseconds >= 200)
                {
                    lastMeterUpdate = DateTime.UtcNow;
                    UpdateParallelState();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Error in parallel scan loop"); }
        finally
        {
            try { process.Kill(true); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to kill scanner process"); }
            process.Dispose();
            if (ReferenceEquals(_scannerProcess, process)) _scannerProcess = null;
        }
        return totalBytesRead;
    }

    /// <summary>
    /// Feed one IQ chunk to every parallel pipeline. Channelizing (the DSP-heavy part) runs
    /// across cores since each pipeline only reads the shared buffer and writes its own state.
    /// Dispatch (decoder feed, squelch, events) then runs sequentially on this thread, because
    /// the event handlers share the tone detector, scanner state and recording service.
    /// Both phases finish before the next read reuses <paramref name="buffer"/>.
    /// </summary>
    private void DistributeIQ(List<ChannelPipeline> pipelines, byte[] buffer, int bytesRead)
    {
        if (pipelines.Count > 1)
        {
            Parallel.For(0, pipelines.Count, i => ChannelizeSafe(pipelines[i], buffer, bytesRead));
        }
        else if (pipelines.Count == 1)
        {
            ChannelizeSafe(pipelines[0], buffer, bytesRead);
        }

        foreach (var pipeline in pipelines)
        {
            try { pipeline.DispatchAudio(); }
            catch (Exception ex) { _logger.LogError(ex, $"Error in pipeline for {pipeline.Channel.AlphaTag}"); }
        }
    }

    private void ChannelizeSafe(ChannelPipeline pipeline, byte[] buffer, int bytesRead)
    {
        try { pipeline.ChannelizeIQ(buffer, bytesRead); }
        catch (Exception ex) { _logger.LogError(ex, $"Error channelizing {pipeline.Channel.AlphaTag}"); }
    }

    /// <summary>
    /// FFT spectrum calculation for UI display only (no carrier detection in parallel mode).
    /// </summary>
    private void ProcessSamplesForSpectrum(byte[] buffer, int length, double centerFreq, int sampleRate, int fftSize = DetectionFftSize)
    {
        // Fewer segments than the detection path: this only feeds the waterfall, and it runs at
        // ~50 Hz on top of every channel pipeline's own filtering.
        var spectrum = SpectrumEstimator.Estimate(buffer, length, sampleRate, fftSize, maxSegments: 4);
        if (spectrum == null) return;

        PublishSpectrum(spectrum, centerFreq, sampleRate);
    }

    /// <summary>
    /// Handle audio from a parallel channel pipeline. Enqueue the channel's mono samples for
    /// the stereo mixer (which sums all active channels into one panned stream) and route the
    /// untouched mono audio to per-channel recording.
    /// </summary>
    private void HandleParallelAudio(Channel channel, byte[] audio)
    {
        // Each channel gets its own tone-detection state: the detector is stateful, and
        // interleaving different channels' chunks into one state breaks Tone A -> Tone B
        // timing and sustained-tone runs.
        _toneDetector.ProcessAudio(audio, channel.Frequency);
        // MDC1200 is decoded per-channel inside each ChannelPipeline (analog modes),
        // which attributes the unit ID to the correct channel; the shared decoder here
        // could neither decode interleaved multi-channel audio nor attribute it.

        // Enqueue mono samples for the mixer instead of broadcasting per-channel stereo.
        _mixer.Enqueue(channel.Frequency, audio);

        // Route mono to multi-channel recording (recordings stay mono per channel)
        if (_recordingService.IsChannelRecording(channel.Frequency))
        {
            _recordingService.ProcessAudio(channel.Frequency, audio);

            // Keep the recording alive while audio is still flowing, not only on
            // decoder metadata/activity events. Otherwise a gap in metadata longer
            // than the 2s activity timeout finalizes the clip mid-transmission and
            // the next event starts a new one, splitting one over into ~2s chunks.
            // Throttled per channel to avoid re-arming the timer on every chunk.
            var now = DateTime.UtcNow;
            var last = _parallelLastAudioReset.TryGetValue(channel.Frequency, out var l) ? l : DateTime.MinValue;
            if ((now - last).TotalMilliseconds >= 500)
            {
                _parallelLastAudioReset[channel.Frequency] = now;
                ResetParallelActivityTimeout(channel);
            }
        }

        UpdateParallelStateThrottled();
    }

    /// <summary>
    /// Handle activity detection from a parallel channel pipeline.
    /// </summary>
    private void HandleParallelActivity(Channel channel, int? src, int? tgt, string? tone)
    {
        _logger.LogInformation($"[Parallel] Activity on {channel.AlphaTag}: src={src} tgt={tgt} tone={tone}");

        // Track per-channel tones
        if (tone != null)
        {
            _parallelLastTones[channel.Frequency] = tone;
            if (tone == "EMRG")
                UpdateState(_state with { LastDetectedTone = "EMRG" });
        }

        // Start recording if not already
        var rs = _recordingService;
        if (!rs.IsChannelRecording(channel.Frequency))
        {
            rs.StartParallelRecording(channel, src, tgt, null);
        }
        else
        {
            // Already recording this channel: update the current SRC/TGT and, on a
            // talker change, append to the speaker chain (AppendSpeaker de-dupes
            // consecutive repeats). Mirrors the single-channel HandleActivity path.
            rs.UpdateSourceTarget(channel.Frequency, src, tgt);
            if (src.HasValue) rs.AppendSpeaker(channel.Frequency, src.Value);
        }

        // Reset per-channel activity timeout
        ResetParallelActivityTimeout(channel);

        UpdateParallelState();
    }

    /// <summary>
    /// Handle activity ending on a parallel channel (analog modes).
    /// </summary>
    private void HandleParallelActivityEnded(Channel channel)
    {
        _logger.LogInformation($"[Parallel] Activity ended on {channel.AlphaTag}");

        _parallelLastTones.TryRemove(channel.Frequency, out var lastTone);
        _recordingService.StopParallelRecording(channel.Frequency, lastTone);

        UpdateParallelState();
    }

    /// <summary>
    /// Reset the per-channel activity timeout. After 2s of no new activity events,
    /// stop recording for that channel.
    /// </summary>
    private void ResetParallelActivityTimeout(Channel channel)
    {
        // Cancel existing timeout for this channel
        if (_parallelActivityTimeouts.TryRemove(channel.Frequency, out var oldCts))
            oldCts.Cancel();

        var cts = new CancellationTokenSource();
        _parallelActivityTimeouts[channel.Frequency] = cts;

        Task.Delay(2000, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                _logger.LogInformation($"[Parallel] Activity timeout on {channel.AlphaTag}");
                _parallelLastTones.TryRemove(channel.Frequency, out var lastTone);
                _recordingService.StopParallelRecording(channel.Frequency, lastTone);
                _parallelActivityTimeouts.TryRemove(channel.Frequency, out _);

                UpdateParallelState();
            }
        });
    }

    /// <summary>
    /// Build and broadcast the ParallelChannelState array to the UI.
    /// </summary>
    private long _lastParallelStateTicks;

    /// <summary>
    /// Per-audio-chunk variant of <see cref="UpdateParallelState"/>: audio arrives for every
    /// active channel on every IQ chunk (~70 Hz each), and rebuilding the state array that often
    /// is wasted work since the broadcaster sends at most every 100 ms. Activity start/end and
    /// the 5 Hz meter tick still call the unthrottled version, so transitions are not delayed.
    /// </summary>
    private void UpdateParallelStateThrottled()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastParallelStateTicks) < 100) return;
        Interlocked.Exchange(ref _lastParallelStateTicks, now);
        UpdateParallelState();
    }

    private void UpdateParallelState()
    {
        if (_parallelPipelines == null) return;

        var states = _parallelPipelines.Select(p => new ParallelChannelState(
            Channel: p.Channel,
            IsActive: p.IsActive,
            SignalStrength: p.SignalLevelDb,
            IsRecording: _recordingService.IsChannelRecording(p.Channel.Frequency),
            SourceID: null,
            TargetID: null,
            SpeakerChain: null,
            CurrentTone: _parallelLastTones.GetValueOrDefault(p.Channel.Frequency),
            MeasuredOffsetHz: p.MeasuredOffsetHz,
            DeviceId: DeviceId
        )).ToArray();

        UpdateState(_state with { ParallelChannels = states });
    }

    private async Task<long> RunSingleScanSegment(double centerFreqMhz, int sampleRate, CancellationToken token, Channel? hopChannel = null)
    {
        // Brief delay to allow the previous rtl_sdr process to fully release the USB device
        await Task.Delay(250, token);

        // Standard stable sample rate for R820T tuner
        int scanRate = 1024000; 
        var binPath = PlatformTools.RtlSdr;
        // -b 1: Use 1 buffer to reduce latency and improve startup reliability
        // A hop to a single channel captures at that channel's own gain class.
        var args = $"-f {centerFreqMhz:F3}M -s {scanRate} {TuningArgs(hopChannel)} -b 1 -";

        _logger.LogInformation($"Starting Scanner: {centerFreqMhz:F3} MHz (Cmd: {binPath} {args})");

        var psi = new ProcessStartInfo(binPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try 
        {
            _scannerProcess = Process.Start(psi);
            UpdateState(_state with { IsHardwareConnected = true });
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to start rtl_sdr scanner process");
             UpdateState(_state with { IsHardwareConnected = false });
             return 0;
        }

        if (_scannerProcess == null) return 0;

        // Monitor Stderr
        _ = Task.Run(async () => {
            try 
            {
                while (!token.IsCancellationRequested && !_scannerProcess.HasExited)
                {
                    var line = await _scannerProcess.StandardError.ReadLineAsync(token);
                    if (string.IsNullOrEmpty(line)) continue;
                    
                    _logger.LogInformation($"Scanner HW: {line}");

                    if (line.Contains("Found") || line.Contains("Using device")) 
                        UpdateState(_state with { IsHardwareConnected = true });
                    // rtl_sdr exits on its own here; RunScannerLoop backs off and retries.
                    if (line.Contains("No supported devices") || line.Contains("Failed to open"))
                        UpdateState(_state with { IsHardwareConnected = false });
                }
            } 
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error monitoring scanner stderr");
            }
        }, token);

        // Read Stdout
        var bufferSize = 16384 * 4; 
        var buffer = new byte[bufferSize];
        var baseStream = _scannerProcess.StandardOutput.BaseStream;
        var lastUpdate = DateTime.MinValue;
        var segmentStartTime = DateTime.UtcNow;

        long totalBytesRead = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Watchdog: If ReadAsync hangs for > 5 seconds, we assume hardware stall
                using var watchdogCts = new CancellationTokenSource();
                var readTask = baseStream.ReadAsync(buffer, 0, buffer.Length, token);
                var timeoutTask = Task.Delay(5000, watchdogCts.Token);

                var completedTask = await Task.WhenAny(readTask, timeoutTask);
                if (completedTask == timeoutTask)
                {
                    bool isAlive = _scannerProcess != null && !_scannerProcess.HasExited;
                    _logger.LogWarning($"Scanner hardware stalled (No data for 5s). Process Alive: {isAlive}, Total Bytes: {totalBytesRead}. Restarting...");
                    // A stall with no data at all is counted by RunScannerLoop's backoff.
                    if (totalBytesRead > 0) System.Threading.Interlocked.Increment(ref _restartCount);
                    break;
                }

                // Data received, cancel the watchdog task
                watchdogCts.Cancel();
                var bytesRead = await readTask;
                if (bytesRead <= 0) 
                {
                    _logger.LogDebug("Scanner process stopped sending data.");
                    break;
                }

                totalBytesRead += bytesRead;
                System.Threading.Interlocked.Add(ref _totalBytesRead, bytesRead);


                // Rate limit FFT updates (approx 50Hz)
                if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds < 20) continue;
                lastUpdate = DateTime.UtcNow;

                // Warm-up: Skip first 200ms of data
                if ((DateTime.UtcNow - segmentStartTime).TotalMilliseconds < 50) continue;

                if (_iqDumpStream != null)
                {
                    await _iqDumpStream.WriteAsync(buffer, 0, bytesRead, token);
                }

                try 
                {
                    ProcessSamples(buffer, bytesRead, centerFreqMhz, scanRate);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing scanner samples");
                }
            }
        }
        catch (OperationCanceledException) 
        {
            // Normal segment end
        }
        catch (Exception ex) { _logger.LogError(ex, "Error in scan loop segment"); }
        finally
        {
            try { _scannerProcess?.Kill(true); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to kill scanner process in finally"); }
            _scannerProcess = null;
        }
        return totalBytesRead;
    }

    /// <inheritdoc cref="ScanBanks.NudgeOffChannels"/>
    internal static double NudgeOffChannels(double center, List<Channel> sorted, double spreadMHz) =>
        ScanBanks.NudgeOffChannels(center, sorted, spreadMHz);

    private List<double> CalculateScanCenters(List<Channel> channels)
    {
        if (!channels.Any()) return new List<double>();
        
        var sorted = channels.OrderBy(c => c.Frequency).ToList();
        var centers = new List<double>();
        
        // Simple greedy clustering
        var currentCluster = new List<Channel>();
        currentCluster.Add(sorted[0]);
        
        for (int i = 1; i < sorted.Count; i++)
        {
            var c = sorted[i];
            // Use a conservative 0.5MHz spread
            if (c.Frequency - currentCluster[0].Frequency > 0.5)
            {
                // Close current cluster
                var center = (currentCluster.Min(x => x.Frequency) + currentCluster.Max(x => x.Frequency)) / 2.0;
                // Offset by 250kHz to be safely away from DC spike but well within bandwidth
                centers.Add(center + 0.25);
                
                currentCluster.Clear();
                currentCluster.Add(c);
            }
            else
            {
                currentCluster.Add(c);
            }
        }
        
        // Add last cluster
        if (currentCluster.Any())
        {
            var center = (currentCluster.Min(x => x.Frequency) + currentCluster.Max(x => x.Frequency)) / 2.0;
            // Offset by 250kHz
            centers.Add(center + 0.25);
        }
        
        return centers;
    }

    /// <summary>
    /// Calculate scan banks using a simple two-mode strategy:
    /// - FastScan: all channels span ≤ 2.4 MHz → single bank, SDR covers all simultaneously, runs continuously
    /// - FrequencyHop: channels span > 2.4 MHz → one bank per channel at its exact frequency, dwell-hop through each
    /// </summary>
    private List<ScanBank> CalculateScanBanks(List<Channel> channels) => ScanBanks.Build(channels, _logger);

    /// <summary>
    /// Bins per detection FFT. At the 1.024 MHz scan rate this is 1 kHz per bin, fine enough
    /// to resolve a 12.5 kHz channel across ~13 bins rather than guessing from one.
    /// </summary>
    private const int DetectionFftSize = 1024;

    /// <summary>Segments averaged per buffer. See <see cref="SpectrumEstimator.Estimate"/>.</summary>
    private const int DetectionSegments = 16;

    private void ProcessSamples(byte[] buffer, int length, double centerFreq, int sampleRate)
    {
        var spectrum = SpectrumEstimator.Estimate(
            buffer, length, sampleRate, DetectionFftSize, DetectionSegments);
        if (spectrum == null) return;

        PublishSpectrum(spectrum, centerFreq, sampleRate);
        CheckChannels(spectrum, centerFreq);
    }

    /// <summary>Pushes a spectrum estimate to the UI waterfall.</summary>
    private void PublishSpectrum(SpectrumEstimate spectrum, double centerFreq, int sampleRate)
    {
        var db = spectrum.ToDbArray();
        var spectrumPoints = new SpectrumPoint[db.Length];
        for (int i = 0; i < db.Length; i++)
        {
            double freqOffset = (i - db.Length / 2.0) * spectrum.BinWidthHz;
            spectrumPoints[i] = new SpectrumPoint((centerFreq * 1e6 + freqOffset) / 1e6, db[i]);
        }
        UpdateState(_state with { RfSpectrum = spectrumPoints });
    }

        // Normalize (Empirical)
    private void CheckChannels(SpectrumEstimate spectrum, double centerFreq)
    {
        Channel? bestChannel = null;
        double bestSnr = double.NegativeInfinity;
        double maxDetectedDb = -100;
        double squelchSnr = _state.Squelch ?? DefaultSquelchSnrDb;

        // Clean up old lockouts to prevent memory leak
        if (!_channelLockouts.IsEmpty)
        {
            var expiredLockouts = _channelLockouts.Where(kvp => DateTime.UtcNow >= kvp.Value).Select(kvp => kvp.Key).ToList();
            if (expiredLockouts.Any())
            {
                foreach (var key in expiredLockouts)
                {
                    if (_channelLockouts.TryRemove(key, out _))
                    {
                        _logger.LogDebug($"Removed expired lockout for frequency {key}");
                    }
                }
            }
        }

        // Only this SDR's channels: another SDR's channel can fall inside this capture's
        // bandwidth, and locking on to it here would steal it from the SDR that carries it.
        foreach (var channel in ScanChannels)
        {
            if (channel.Avoid) continue;

            // Check for channel lockout
            if (_channelLockouts.TryGetValue(channel.Frequency, out var lockoutUntil) && DateTime.UtcNow < lockoutUntil)
            {
                _logger.LogInformation($"--> Channel {channel.AlphaTag} ({channel.Frequency} MHz) is currently locked out until {lockoutUntil:HH:mm:ss}. Skipping.");
                continue;
            }

            int binIndex = spectrum.BinForOffset((channel.Frequency - centerFreq) * 1e6);
            if (binIndex < 0) continue;

            // The residual DC spike is a single bin wide once the capture has been DC-blocked,
            // so only that bin is skipped. The old guard blanked three bins, and at the FFT
            // size in use that blinded a ~28 kHz window around the tuner centre — enough to
            // hide a channel outright when a FastScan bank happened to centre on one.
            if (binIndex == spectrum.FftSize / 2)
            {
                _logger.LogDebug(
                    $"{channel.AlphaTag} ({channel.Frequency} MHz) sits on the tuner centre; " +
                    "skipping its DC bin.");
                continue;
            }

            int halfWidth = spectrum.HalfWidthBins(ChannelBandwidthHz(channel.Mode));
            double snr = spectrum.ChannelSnrDb(binIndex, halfWidth);
            double db = spectrum.ChannelPowerDb(binIndex, halfWidth);
            if (db > maxDetectedDb) maxDetectedDb = db;

            if (snr <= squelchSnr)
            {
                _channelHits[channel.Frequency] = 0;
                continue;
            }

            _channelHits[channel.Frequency] = _channelHits.GetValueOrDefault(channel.Frequency, 0) + 1;

            // Averaging 16 segments cut the per-bin variance by 16x, so a single reading is
            // now trustworthy on its own for anything comfortably above threshold. Only
            // marginal signals still wait for a second look.
            int hitsNeeded = snr > squelchSnr + 10 ? 1 : 2;

            if (_channelHits[channel.Frequency] >= hitsNeeded && snr > bestSnr)
            {
                bestSnr = snr;
                bestChannel = channel;
            }
        }

        // Map dB to 0-100% (Assumes -60dB noise floor, 0dB max)
        double strength = Math.Clamp((maxDetectedDb + 60) / 60.0 * 100, 0, 100);
        UpdateState(_state with { CurrentSignalDb = maxDetectedDb, SignalStrength = strength });

        if (bestChannel != null)
        {
            _logger.LogInformation(
                $"Detected carrier on {bestChannel.AlphaTag} (SNR: {bestSnr:F1}dB over a " +
                $"{spectrum.NoiseFloorDb:F1}dB floor)");
            StopScanning();
            _recordingLockoutUntil = DateTime.UtcNow.AddSeconds(3);
            LockOn(bestChannel);
        }
    }

    /// <summary>
    /// Occupied bandwidth of a channel by mode, used to decide how many FFT bins its energy
    /// is spread across. Analog FM stays at 25 kHz rather than assuming a narrowbanded system:
    /// integrating a slightly too-wide window costs a little noise, whereas integrating too
    /// narrow a one misses signal.
    /// </summary>
    private static double ChannelBandwidthHz(string? mode) => mode?.ToUpperInvariant() switch
    {
        "P25" or "DMR" => 12500,
        "AM" => 10000,
        _ => 25000,
    };

    private void LockOn(Channel channel)
    {
        _logger.LogInformation($"Locked on to {channel.AlphaTag} ({channel.Frequency} MHz)");

        UpdateState(_state with 
        { 
            Status = _manualOverride ? "MONITORING" : "RECEIVING",
            CurrentFrequency = channel.Frequency,
            CurrentChannel = channel,
            IsAudioStreaming = true,
            ParallelChannels = null
        });

        // Brief delay to let the USB device release after scanner stop before decoder claims it
        Task.Delay(150).ContinueWith(_ => {
            // Guard: only start if we're still locked to this channel
            if (_state.Status != "RECEIVING" && _state.Status != "MONITORING") return;
            if (_state.CurrentChannel?.Frequency != channel.Frequency) return;
            StartDecoding(channel);
        });

        // Safety timeout — DMR needs more time to sync (TDMA framing + AMBE vocoder)
        if (!_manualOverride)
        {
            int initialTimeout = channel.Mode == "DMR" ? 20000 : 10000;
            RestartSessionTimeout(initialTimeout);
        }
    }

    private void RestartSessionTimeout(int ms)
    {
        _sessionTimeoutCts?.Cancel();
        _sessionTimeoutCts = new CancellationTokenSource();
        var token = _sessionTimeoutCts.Token;

        Task.Delay(ms, token).ContinueWith(_ => 
        {
            if (!token.IsCancellationRequested)
            {
                // If we are currently recording, don't timeout
                if (IsRecordingLockedChannel) return;

                _logger.LogInformation("Session timeout (no data), resuming scan...");
                StopDecoding();
                UpdateState(_state with { Status = "IDLE" });
                StartScanning();
            }
        });
    }

    private void StopDecoding()
    {
        _decodeCts?.Cancel();
        if (_state.CurrentChannel != null)
        {
            _recordingService.StopParallelRecording(_state.CurrentChannel.Frequency, _lastDetectedTone);
            // Clear the detected tone so it doesn't bleed onto the next transmission.
            _lastDetectedTone = null;
        }
        if (_lockedAudioFrequency is double locked)
        {
            _mixer.Close(locked);
            _toneDetector.ResetStream(locked);
            _lockedAudioFrequency = null;
        }

        if (_currentDecoder != null)
        {
            _currentDecoder.Stop();
            _currentDecoder = null;
        }
        
        // Clear the pre-roll buffer when leaving a channel so that stale audio from
        // the outgoing frequency cannot bleed into the next transmission's recording.
        lock (_preRollBuffer)
        {
            _preRollBuffer.Clear();
        }
    }

    private void StartDecoding(Channel channel)
    {
        StopDecoding();

        // Reset per-call state for the new channel.
        UpdateState(_state with { SourceID = null, TargetID = null, SpeakerChain = null });
        
        _decodeCts = new CancellationTokenSource();
        var token = _decodeCts.Token;

        try 
        {
            _currentDecoder = _decoderFactory.GetDecoder(channel.Mode);
            // The decoder runs its own rtl_fm, so it needs the same front-end settings as the
            // wideband scan that found the signal — otherwise the scanner detects at one gain
            // and listens at another.
            _currentDecoder.Tuning = CurrentTuning(channel);

            _lockedAudioFrequency = channel.Frequency;
            _mixer.Open(channel.Frequency);

            _currentDecoder.OnAudio += (chunk) => 
            {
                // Maintain pre-roll buffer
                lock (_preRollBuffer)
                {
                    _preRollBuffer.AddLast(chunk);
                    long currentSize = 0;
                    foreach (var b in _preRollBuffer) currentSize += b.Length;
                    while (currentSize > PreRollMaxBytes && _preRollBuffer.First != null)
                    {
                        currentSize -= _preRollBuffer.First.Value.Length;
                        _preRollBuffer.RemoveFirst();
                    }
                }

                // Analyze for Fire Tone Outs and MDC1200 signaling. The tone detector is shared
                // with other SDRs, so this channel gets its own keyed stream.
                _toneDetector.ProcessAudio(chunk, channel.Frequency);
                _mdc.ProcessAudio(chunk);
                _mixer.Enqueue(channel.Frequency, chunk);

                _recordingService.ProcessAudio(channel.Frequency, chunk);

                if (IsRecordingLockedChannel)
                {
                    ResetActivityTimeout(); 
                }
            };

            _currentDecoder.OnActivity += (src, tgt, tone) => HandleActivity(channel, src, tgt, tone);
            _currentDecoder.OnMetadata += (line) => _logger.LogInformation($"Decoder: {line}");

            // Start safely
            Task.Run(async () => 
            {
                try 
                {
                    await _currentDecoder.StartAsync(channel, token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Decoder error");
                    if (_state.Status == "RECEIVING") UpdateState(_state with { Status = "IDLE" });
                }
            }, token);
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to initialize decoder");
             return;
        }
    }

    private void ResetActivityTimeout()
    {
        // Throttle updates to avoid task thrashing (e.g. max 2 times per second)
        if ((DateTime.UtcNow - _lastActivityReset).TotalMilliseconds < 500) return;
        _lastActivityReset = DateTime.UtcNow;
        ScheduleStopTimeout();
    }

    // (Re)schedule the timer that finalizes the current transmission once audio
    // stops. Normally 2s of silence ends a transmission; once a fire tone-out has
    // been detected for this transmission we wait ToneHoldMs of silence instead,
    // so the tone and the following dispatch voice are captured as a single
    // transmission (with the speech available to transcribe). Because this timer
    // is re-armed on every audio chunk, the hold is measured from the last audio
    // — i.e. from when the tone actually ends — not from when it was detected.
    private void ScheduleStopTimeout()
    {
        _activityTimeoutCts?.Cancel();
        _activityTimeoutCts = new CancellationTokenSource();
        var token = _activityTimeoutCts.Token;

        if (!_manualOverride)
        {
            RestartSessionTimeout(5000); // 5s hang time
        }

        bool withinToneHold = _lastDetectedTone != null;
        int stopDelayMs = withinToneHold ? ToneHoldMs : ActivityTimeoutMs;

        Task.Delay(stopDelayMs, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                if (_state.CurrentChannel != null)
                {
                    _recordingService.StopParallelRecording(_state.CurrentChannel.Frequency, _lastDetectedTone);
                    // Clear the detected tone so it doesn't bleed onto the next transmission.
                    _lastDetectedTone = null;
                }

                if (_manualOverride) UpdateState(_state with { Status = "MONITORING" });
            }
        });
    }

    private void HandleActivity(Channel originChannel, int? src = null, int? tgt = null, string? tone = null)
    {
        // STRICT ATTRIBUTION: Ignore activity if we have moved to a different channel
        if (_state.CurrentChannel == null || Math.Abs(_state.CurrentChannel.Frequency - originChannel.Frequency) > 0.001) return;

        // Capture the previous source ID before UpdateState overwrites it.
        var prevSourceID = _state.SourceID;

        // Build the live speaker chain for the UI: append when the talker changes.
        string? updatedChain = _state.SpeakerChain;
        if (src.HasValue && src != prevSourceID)
        {
            updatedChain = updatedChain == null
                ? src.Value.ToString()
                : $"{updatedChain} → {src.Value}";
        }

        // Valid Activity detected
        if (_state.Status != "RECEIVING" || 
            (src.HasValue && _state.SourceID != src) || 
            (tone != null && _state.CurrentTone != tone))
        {
            UpdateState(_state with { 
                Status = "RECEIVING", 
                SourceID = src ?? _state.SourceID, 
                TargetID = tgt ?? _state.TargetID,
                SpeakerChain = updatedChain,
                CurrentTone = tone ?? _state.CurrentTone,
                LastTranscription = null 
            });
        }

        // Emergency calls route the tone to _lastDetectedTone so it is persisted
        // to the DB (normally only fire tones flow through that path).
        if (tone == "EMRG")
            _lastDetectedTone = "EMRG";
        
        // Start recording if not already started. Recordings are keyed by channel so that
        // several SDRs can each record what they are locked on to at the same time.
        var freq = originChannel.Frequency;
        if (!_recordingService.IsChannelRecording(freq))
        {
            _recordingService.StartParallelRecording(originChannel, src, tgt, _preRollBuffer);
            // Clear the pre-roll buffer after flushing it into the recording so that
            // subsequent transmissions do not inherit audio from this one.
            lock (_preRollBuffer)
            {
                _preRollBuffer.Clear();
            }
        }
        else if (src.HasValue)
        {
            if (!prevSourceID.HasValue)
            {
                // Late-arriving IDs — first activity event fired before dsd-fme emitted TGT/SRC.
                _recordingService.UpdateSourceTarget(freq, src, tgt);
            }
            else if (src != prevSourceID)
            {
                // Talker changed — append to the speaker chain within the same recording.
                _recordingService.AppendSpeaker(freq, src.Value);
            }
        }
        else if (tgt.HasValue && !prevSourceID.HasValue)
        {
            _recordingService.UpdateSourceTarget(freq, null, tgt);
        }

        ResetActivityTimeout();
    }
}
