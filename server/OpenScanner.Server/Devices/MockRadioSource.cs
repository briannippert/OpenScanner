using OpenScanner.Server.Audio;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;

namespace OpenScanner.Server.Devices;

/// <summary>
/// A hardware-free radio source that replays a list of <see cref="ScenarioEvent"/>s
/// as if they were live transmissions. Used both to demo the app without an
/// RTL-SDR dongle and to back the device tests.
///
/// Timing is driven entirely through an injected <see cref="TimeProvider"/> and
/// audio through an injected <see cref="IMockAudioProvider"/>, so tests can run
/// on a <c>FakeTimeProvider</c> with synthetic audio for fast, deterministic
/// results, while the running app uses the system clock and real file playback.
/// </summary>
public class MockRadioSource : BackgroundService, IRadioSource
{
    // Loop keeps running this long past the last event before restarting the scenario.
    private const double LoopTailSeconds = 10;
    private const double FreqMatchTolerance = 0.001;

    private readonly ILogger<MockRadioSource> _logger;
    private readonly ToneDetector _toneDetector;
    private readonly Mdc1200Decoder _mdc;
    private readonly IDatabase _db;
    private readonly IRecordingService _recordingService;
    private readonly IChannelService _channelService;
    private readonly IMockAudioProvider _audioProvider;
    private readonly TimeProvider _timeProvider;

    public event Action<ScannerState>? OnStateChanged;
    public event Action<CallLog>? OnNewLog;
    public event Action<AudioChunk>? OnAudio;
    public event Action<RadioEvent>? OnNewEvent;

    private ScannerState _state = new("IDLE", 0);
    private List<ScenarioEvent> _scenarioEvents = new();
    private readonly Dictionary<double, DateTimeOffset> _avoidUntil = new();

    private DateTimeOffset _startTime;
    private bool _isRunning;
    private bool _manualOverride;
    private ScenarioEvent? _lockedEvent; // the event we have already locked onto / streamed
    private CancellationTokenSource? _currentTaskCts;
    private Task? _simulationTask;

    // Number of times the loop has parked on a timer. Lets tests deterministically
    // wait for the loop to reach a known-quiescent point before advancing the clock.
    private int _parkCount;

    // Audio pre-roll buffer (rolling window of recent audio).
    private readonly LinkedList<byte[]> _preRollBuffer = new();
    private const int PreRollMaxBytes = 96000 * 2;

    public MockRadioSource(
        ILogger<MockRadioSource> logger,
        ToneDetector toneDetector,
        Mdc1200Decoder mdc,
        IDatabase db,
        IRecordingService recordingService,
        IChannelService channelService,
        IMockAudioProvider audioProvider,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _toneDetector = toneDetector;
        _mdc = mdc;
        _db = db;
        _recordingService = recordingService;
        _channelService = channelService;
        _audioProvider = audioProvider;
        _timeProvider = timeProvider;

        _recordingService.OnNewLog += log => OnNewLog?.Invoke(log);

        _toneDetector.OnToneDetected += tone =>
        {
            UpdateState(_state with { LastDetectedTone = tone.Name });
            RaiseRadioEvent(new RadioEvent
            {
                Type = "TONE_OUT",
                Label = tone.Name,
                ToneA = tone.FrequencyA,
                ToneB = tone.FrequencyB
            });
        };

        _mdc.OnPacket += pkt =>
        {
            // Attribute the MDC1200 unit ID to the current transmission like a P25 radio ID.
            UpdateState(_state with { SourceID = pkt.UnitId });
        };

        LoadDefaultScenario();
    }

    /// <summary>
    /// Stamps channel context onto a detected signaling event, persists it, and broadcasts it.
    /// </summary>
    private void RaiseRadioEvent(RadioEvent e)
    {
        e.Id = Guid.NewGuid().ToString();
        e.Timestamp = _timeProvider.GetUtcNow().UtcDateTime.ToString("o");
        e.Frequency = _state.CurrentChannel?.Frequency ?? _state.CurrentFrequency ?? 0;
        e.AlphaTag = _state.CurrentChannel?.AlphaTag;
        // Link the event to the recording it coincides with (if any) so the UI can
        // jump to it in the transmission history.
        e.TransmissionId ??= _recordingService.CurrentRecordingId;

        _db.AddRadioEventAsync(e).ContinueWith(
            t => _logger.LogError(t.Exception, "Failed to persist radio event"),
            TaskContinuationOptions.OnlyOnFaulted);

        OnNewEvent?.Invoke(e);
    }

    /// <summary>Number of times the simulation loop has parked waiting for time to pass.</summary>
    public int ParkCount => Volatile.Read(ref _parkCount);

    public void SetScenario(List<ScenarioEvent> events) => _scenarioEvents = events;

    public ScannerState GetState() => _state;

    /// <inheritdoc />
    public RadioDiagnostics GetDiagnostics() => new RadioDiagnostics(0, 0);

    public void ReloadChannels() => _channelService.ReloadChannels();

    public void SetSquelch(double db) => UpdateState(_state with { Squelch = db });

    public void SetGain(double db) => UpdateState(_state with { Gain = db });

    public void SetPpm(double ppm) => UpdateState(_state with { Ppm = ppm });

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        _startTime = _timeProvider.GetUtcNow();
        _manualOverride = false;
        _lockedEvent = null;
        UpdateState(_state with { Status = "SCANNING", IsHardwareConnected = true });

        _currentTaskCts = new CancellationTokenSource();
        _simulationTask = Task.Run(() => SimulationLoop(_currentTaskCts.Token));
    }

    public void Stop()
    {
        _isRunning = false;
        _currentTaskCts?.Cancel();
        UpdateState(_state with { Status = "IDLE", IsHardwareConnected = true });
    }

    /// <summary>Cancels the simulation loop and awaits its completion. Intended for shutdown/tests.</summary>
    public async Task StopAsync()
    {
        Stop();
        if (_simulationTask != null)
        {
            try { await _simulationTask; }
            catch (OperationCanceledException) { }
        }
    }

    public void HoldFrequency(double freq)
    {
        _manualOverride = true;
        _lockedEvent = null;
        UpdateState(_state with { ManualHoldFrequency = freq, Status = "MONITORING", CurrentFrequency = freq });
    }

    public void ResumeScan()
    {
        _manualOverride = false;
        _lockedEvent = null;
        UpdateState(_state with { Status = "SCANNING", ManualHoldFrequency = null });
    }

    public void StartDebugSpectrum(double freq, double? gain = null)
    {
        UpdateState(_state with { Status = "DEBUG", CurrentFrequency = freq, RfSpectrum = null, Gain = gain });
    }

    public void AvoidFrequency(double freq, double durationSeconds)
    {
        _avoidUntil[freq] = _timeProvider.GetUtcNow().AddSeconds(durationSeconds);
        _logger.LogInformation("Mock: Avoiding {Frequency} for {Duration}s", freq, durationSeconds);
    }

    public void StartDumping(string label) { }
    public void StopDumping() { }

    public byte[][] GetPreRollBuffer()
    {
        lock (_preRollBuffer)
        {
            return _preRollBuffer.ToArray();
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Auto-start the simulation when hosted by the app.
        Start();
        return Task.CompletedTask;
    }

    /// <summary>Total length of the scenario (end time of its last event), or 0 if empty.</summary>
    internal double ScenarioLength =>
        _scenarioEvents.Count > 0 ? _scenarioEvents.Max(e => e.Time + e.Duration) : 0;

    /// <summary>
    /// Pure lookup: the event active at <paramref name="elapsedSeconds"/> into the
    /// scenario, or null. No timing, threading, or side effects — directly unit-testable.
    /// </summary>
    internal ScenarioEvent? GetActiveEvent(double elapsedSeconds) =>
        _scenarioEvents.FirstOrDefault(e =>
            elapsedSeconds >= e.Time && elapsedSeconds < e.Time + e.Duration);

    /// <summary>Whether <paramref name="freq"/> is currently within an active avoid window.</summary>
    internal bool IsAvoided(double freq)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var (avoidFreq, until) in _avoidUntil)
        {
            if (Math.Abs(avoidFreq - freq) < FreqMatchTolerance && until > now)
                return true;
        }
        return false;
    }

    private async Task SimulationLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _isRunning)
            {
                var elapsed = (_timeProvider.GetUtcNow() - _startTime).TotalSeconds;

                // Loop the scenario once we've run past the end (plus a tail).
                if (ScenarioLength > 0 && elapsed > ScenarioLength + LoopTailSeconds)
                {
                    _startTime = _timeProvider.GetUtcNow();
                    _lockedEvent = null;
                    _logger.LogInformation("[MockRadioSource] Scenario loop resetting.");
                    elapsed = 0;
                }

                var active = GetActiveEvent(elapsed);
                var canHear = active != null && CanHear(active);

                if (canHear)
                {
                    await ReceiveAsync(active!, token);
                }
                else
                {
                    LoseSignalIfReceiving();
                }

                await DelayUntilNextBoundary(elapsed, active, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { /* normal during shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MockRadioSource] Simulation loop error");
        }
    }

    private bool CanHear(ScenarioEvent evt)
    {
        if (_manualOverride)
        {
            // In manual hold we only hear the frequency we're parked on.
            return Math.Abs((_state.ManualHoldFrequency ?? 0) - evt.Frequency) < FreqMatchTolerance;
        }

        // While scanning, a temporarily-avoided or channel-avoided frequency is skipped.
        if (IsAvoided(evt.Frequency)) return false;

        var channel = FindChannel(evt.Frequency);
        return channel is not { Avoid: true };
    }

    private async Task ReceiveAsync(ScenarioEvent evt, CancellationToken token)
    {
        // Lock on the first time we hear this event instance.
        if (!ReferenceEquals(_lockedEvent, evt))
        {
            _lockedEvent = evt;
            var channel = FindChannel(evt.Frequency)
                ?? new Channel(evt.Frequency, "Mock", "Mock Channel", evt.DecoderType ?? "FM", "FM");

            UpdateState(_state with
            {
                Status = _manualOverride ? "MONITORING" : "RECEIVING",
                CurrentFrequency = evt.Frequency,
                CurrentChannel = channel,
                SourceID = evt.SourceId,
                TargetID = evt.TargetId
            });

            if (!_recordingService.IsRecording)
            {
                ClearPreRoll();
                _recordingService.StartRecording(channel, evt.SourceId, evt.TargetId, _preRollBuffer);
            }

            await _audioProvider.StreamAsync(evt, HandleAudioChunk, token);
        }
    }

    private void LoseSignalIfReceiving()
    {
        if (_lockedEvent == null) return;

        var channel = _state.CurrentChannel;
        _lockedEvent = null;
        _recordingService.StopRecording(channel!, null);

        if (!_manualOverride)
        {
            // Back to scanning; manual hold stays parked on its frequency.
            UpdateState(_state with { Status = "SCANNING", CurrentChannel = null });
        }
    }

    /// <summary>
    /// Delays until the next scenario transition (current event's end, or the next
    /// event's start, or the loop reset point). Precise scheduling means transitions
    /// land exactly instead of being quantized to a poll interval.
    /// </summary>
    private async Task DelayUntilNextBoundary(double elapsed, ScenarioEvent? active, CancellationToken token)
    {
        double next;
        if (active != null)
        {
            next = active.Time + active.Duration; // wait out the current event
        }
        else
        {
            double? nextStart = null;
            foreach (var e in _scenarioEvents)
            {
                if (e.Time > elapsed && (nextStart == null || e.Time < nextStart))
                    nextStart = e.Time;
            }
            next = nextStart ?? (ScenarioLength > 0 ? ScenarioLength + LoopTailSeconds : elapsed + LoopTailSeconds);
        }

        var waitSeconds = Math.Max(next - elapsed, 0.001);
        await ParkAsync(TimeSpan.FromSeconds(waitSeconds), token);
    }

    /// <summary>
    /// Waits <paramref name="delay"/> of (possibly virtual) time. The timer is
    /// registered before <see cref="_parkCount"/> is bumped, so once a test observes
    /// the incremented count it knows the timer exists and it's safe to advance the clock.
    /// </summary>
    private async Task ParkAsync(TimeSpan delay, CancellationToken token)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = _timeProvider.CreateTimer(_ => tcs.TrySetResult(), null, delay, Timeout.InfiniteTimeSpan);
        using var registration = token.Register(() => tcs.TrySetResult());

        Interlocked.Increment(ref _parkCount);
        await tcs.Task;
        token.ThrowIfCancellationRequested();
    }

    private void HandleAudioChunk(byte[] chunk)
    {
        OnAudio?.Invoke(AudioChunk.Mono48k(chunk));
        _recordingService.ProcessAudio(chunk);
        _toneDetector.ProcessAudio(chunk);
        _mdc.ProcessAudio(chunk);
        AppendPreRoll(chunk);
    }

    private Channel? FindChannel(double frequency) =>
        _channelService.Channels.FirstOrDefault(c => Math.Abs(c.Frequency - frequency) < FreqMatchTolerance);

    private void AppendPreRoll(byte[] chunk)
    {
        lock (_preRollBuffer)
        {
            _preRollBuffer.AddLast(chunk);
            var total = _preRollBuffer.Sum(b => b.Length);
            while (total > PreRollMaxBytes && _preRollBuffer.First != null)
            {
                total -= _preRollBuffer.First.Value.Length;
                _preRollBuffer.RemoveFirst();
            }
        }
    }

    private void ClearPreRoll()
    {
        lock (_preRollBuffer)
        {
            _preRollBuffer.Clear();
        }
    }

    private void UpdateState(ScannerState newState)
    {
        _state = newState;
        OnStateChanged?.Invoke(_state);
    }

    private void LoadDefaultScenario()
    {
        var scenarioPath = Path.Combine(AppContext.BaseDirectory, "TestData", "scenario.json");
        if (!File.Exists(scenarioPath)) return;

        try
        {
            var json = File.ReadAllText(scenarioPath);
            var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            try
            {
                var events = System.Text.Json.JsonSerializer.Deserialize<List<ScenarioEvent>>(json, options);
                if (events != null) _scenarioEvents = events;
            }
            catch (System.Text.Json.JsonException)
            {
                var wrapper = System.Text.Json.JsonSerializer.Deserialize<ScenarioWrapper>(json, options);
                if (wrapper?.Events != null) _scenarioEvents = wrapper.Events;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load default scenario");
        }
    }

    private class ScenarioWrapper
    {
        public List<ScenarioEvent>? Events { get; set; }
    }
}
