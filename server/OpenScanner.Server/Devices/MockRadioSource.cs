using System.Text.Json;
using OpenScanner.Server.Models;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Services;

namespace OpenScanner.Server.Devices;

/// <summary>
/// A simulated radio source that generates events based on a JSON scenario file.
/// </summary>
public class MockRadioSource : BackgroundService, IRadioSource
{
    private readonly ILogger<MockRadioSource> _logger;
    private readonly IDatabase _db;
    private readonly GpsService _gps;
    private readonly ToneDetector _toneDetector;

    /// <inheritdoc />
    public event Action<ScannerState>? OnStateChanged;
    
    /// <inheritdoc />
    public event Action<CallLog>? OnNewLog;
    
    /// <inheritdoc />
    public event Action<byte[]>? OnAudio;

    private ScannerState _state;
    private List<Channel> _channels = new();
    private bool _isScanning = false;
    private bool _manualHold = false;
    private double? _holdFrequency;
    private CancellationTokenSource? _playbackCts;
    
    private List<ScenarioEvent> _scenarioEvents = new();
    private DateTime _scenarioStartTime;

    /// <summary>
    /// Initializes a new instance of the <see cref="MockRadioSource"/> class.
    /// </summary>
    public MockRadioSource(ILogger<MockRadioSource> logger, IDatabase db, GpsService gps, ToneDetector toneDetector)
    {
        _logger = logger;
        _db = db;
        _gps = gps;
        _toneDetector = toneDetector;
        _state = new ScannerState("IDLE", 0);

        _gps.OnGpsUpdate += (data) =>
        {
            UpdateState(_state with { Gps = data });
        };
        
        ReloadChannels();
        LoadScenario();
    }

    private void LoadScenario()
    {
        try
        {
            var searchPaths = new[]
            {
                // 1. Current directory (Direct/Deployment)
                Path.Combine(Directory.GetCurrentDirectory(), "scenario.json"),
                // 2. TestData folder (Test environment)
                Path.Combine(Directory.GetCurrentDirectory(), "TestData", "scenario.json"),
                // 3. Relative to Test Project (Development)
                Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "../OpenScanner.Tests/TestData", "scenario.json"))
            };

            string? path = searchPaths.FirstOrDefault(File.Exists);

            if (path != null)
            {
                var json = File.ReadAllText(path);
                var scenario = JsonSerializer.Deserialize<ScenarioConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (scenario?.Events != null)
                {
                    _scenarioEvents = scenario.Events.OrderBy(e => e.Time).ToList();
                    _logger.LogInformation($"[Mock] Loaded {_scenarioEvents.Count} events from {path}");
                }
            }
            else
            {
                _logger.LogWarning("[Mock] No scenario.json found in searched paths.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Mock] Failed to load scenario.json");
        }
    }

    /// <summary>
    /// Manually sets the scenario events (used for testing).
    /// </summary>
    /// <param name="events">List of scenario events.</param>
    public void SetScenario(List<ScenarioEvent> events)
    {
        _scenarioEvents = events.OrderBy(e => e.Time).ToList();
        _logger.LogInformation($"[Mock] Scenario updated manually with {_scenarioEvents.Count} events.");
    }

    /// <inheritdoc />
    public ScannerState GetState() => _state;

    /// <inheritdoc />
    public void ReloadChannels()
    {
        Task.Run(async () => {
            _channels = (await _db.GetAllChannelsAsync()).ToList();
            _logger.LogInformation($"[Mock] Loaded {_channels.Count} channels.");
        });
    }

    /// <inheritdoc />
    public void SetSquelch(double db)
    {
        UpdateState(_state with { Squelch = db });
        _logger.LogInformation($"[Mock] Squelch set to {db}dB");
    }

    /// <inheritdoc />
    public void Start()
    {
        _logger.LogInformation("[Mock] Starting Scanner...");
        _isScanning = true;
        _scenarioStartTime = DateTime.UtcNow;
        UpdateState(_state with { Status = "SCANNING", IsHardwareConnected = true });
    }

    /// <inheritdoc />
    public void Stop()
    {
        _logger.LogInformation("[Mock] Stopping Scanner...");
        _isScanning = false;
        _manualHold = false;
        _playbackCts?.Cancel();
        UpdateState(_state with { Status = "IDLE", CurrentFrequency = null, CurrentChannel = null, SignalStrength = 0 });
    }

    /// <inheritdoc />
    public void HoldFrequency(double freq)
    {
        _logger.LogInformation($"[Mock] Holding on {freq} MHz");
        _manualHold = true;
        _holdFrequency = freq;
        var channel = _channels.FirstOrDefault(c => Math.Abs(c.Frequency - freq) < 0.001);
        UpdateState(_state with { Status = "MONITORING", CurrentFrequency = freq, CurrentChannel = channel, ManualHoldFrequency = freq });
        
        // If there's an active event on this frequency, start playback
        CheckForActiveEvents();
    }

    /// <inheritdoc />
    public void ResumeScan()
    {
        _logger.LogInformation("[Mock] Resuming scan");
        _manualHold = false;
        _holdFrequency = null;
        UpdateState(_state with { Status = "SCANNING", CurrentFrequency = null, CurrentChannel = null, ManualHoldFrequency = null });
    }

    /// <inheritdoc />
    public void StartDumping(string label)
    {
        _logger.LogInformation($"[Mock] Start dumping requested for {label} (Ignored)");
    }

    /// <inheritdoc />
    public void StopDumping()
    {
        _logger.LogInformation("[Mock] Stop dumping requested (Ignored)");
    }

    /// <summary>
    /// Executes the background task simulating radio activity.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Mock] Background service started.");
        
        // Initial start
        Start();

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_isScanning)
            {
                CheckForActiveEvents();
            }

            await Task.Delay(100, stoppingToken);
        }
    }

    private void CheckForActiveEvents()
    {
        if (!_isScanning) return;

        var elapsed = (DateTime.UtcNow - _scenarioStartTime).TotalSeconds;
        var activeEvent = _scenarioEvents.FirstOrDefault(e => elapsed >= e.Time && elapsed <= (e.Time + e.Duration));

        if (activeEvent != null)
        {
            // If we are scanning and not holding, we "discover" the event
            if (!_manualHold && _state.Status == "SCANNING")
            {
                var channel = _channels.FirstOrDefault(c => Math.Abs(c.Frequency - activeEvent.Frequency) < 0.001);
                if (channel != null)
                {
                    _logger.LogInformation($"[Mock] Detected signal on {channel.AlphaTag} ({activeEvent.Frequency} MHz)");
                    LockOn(channel, activeEvent);
                }
            }
            // If we are holding on this frequency
            else if (_manualHold && _holdFrequency.HasValue && Math.Abs(_holdFrequency.Value - activeEvent.Frequency) < 0.001 && _state.Status != "RECEIVING")
            {
                var channel = _channels.FirstOrDefault(c => Math.Abs(c.Frequency - activeEvent.Frequency) < 0.001);
                LockOn(channel ?? new Channel { Frequency = activeEvent.Frequency, AlphaTag = "Unknown" }, activeEvent);
            }
        }
        else if (_state.Status == "RECEIVING" && !_manualHold)
        {
            // Event ended, resume scanning
            _logger.LogInformation("[Mock] Signal lost, resuming scan...");
            _playbackCts?.Cancel();
            UpdateState(_state with { Status = "SCANNING", CurrentFrequency = null, CurrentChannel = null });
        }
        else if (_state.Status == "RECEIVING" && _manualHold)
        {
             // Event ended but we are holding
             _logger.LogInformation("[Mock] Signal lost (Hold)");
             _playbackCts?.Cancel();
             UpdateState(_state with { Status = "MONITORING" });
        }
    }

    private void LockOn(Channel channel, ScenarioEvent ev)
    {
        UpdateState(_state with { 
            Status = "RECEIVING", 
            CurrentFrequency = channel.Frequency, 
            CurrentChannel = channel,
            SourceID = ev.SourceId,
            TargetID = ev.TargetId,
            SignalStrength = 100,
            CurrentSignalDb = -40
        });

        _playbackCts?.Cancel();
        _playbackCts = new CancellationTokenSource();
        Task.Run(() => PlayAudio(ev.AudioFile, _playbackCts.Token));
    }

    private async Task PlayAudio(string? audioFile, CancellationToken token)
    {
        if (string.IsNullOrEmpty(audioFile)) return;

        var searchPaths = new[]
        {
            // 1. Relative to Test Project (Development/Server run)
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "../OpenScanner.Tests/TestData", audioFile)),
            // 2. Relative to Execution Directory (Test Runner / Copied Output)
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestData", audioFile)),
            // 3. Absolute/Direct path
            audioFile
        };

        string? path = null;
        foreach (var p in searchPaths)
        {
            if (File.Exists(p))
            {
                path = p;
                break;
            }
        }

        if (path == null)
        {
            _logger.LogWarning($"[Mock] Audio file not found: {audioFile}. Checked: {string.Join(", ", searchPaths)}");
            return;
        }

        _logger.LogInformation($"[Mock] Playing audio from: {path}");

        try
        {
            // We expect 48k 16-bit Mono PCM for simplicity in mocking (common for the app)
            // But if it's a WAV, we might need to skip header or use a proper reader.
            // For now, let's assume raw or simple WAV.
            byte[] audioData = await File.ReadAllBytesAsync(path, token);
            int offset = audioData.Length > 44 ? 44 : 0; // Skip WAV header if likely present

            int chunkSize = 4096;
            for (int i = offset; i < audioData.Length; i += chunkSize)
            {
                if (token.IsCancellationRequested) break;

                int length = Math.Min(chunkSize, audioData.Length - i);
                byte[] chunk = new byte[length];
                Array.Copy(audioData, i, chunk, 0, length);

                _toneDetector.ProcessAudio(chunk);
                OnAudio?.Invoke(chunk);

                // Simulate real-time (48000 samples/sec * 2 bytes/sample = 96000 bytes/sec)
                // 4096 bytes / 96000 bytes/sec = 0.0426 sec = 42.6 ms
                await Task.Delay(42, token);
            }

            if (!token.IsCancellationRequested)
            {
                var channel = _state.CurrentChannel;
                if (channel != null)
                {
                    var log = new CallLog(
                        $"mock_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                        DateTime.UtcNow.ToString("o"),
                        channel.Frequency,
                        channel.AlphaTag,
                        channel.Description,
                        _state.Gps?.Lat,
                        _state.Gps?.Lon,
                        audioFile,
                        (audioData.Length - offset) / 96000.0,
                        "Mock Transcription",
                        _state.SourceID,
                        _state.TargetID,
                        null
                    );
                    OnNewLog?.Invoke(log);
                }
            }
        }
        catch (OperationCanceledException) 
        {
            _logger.LogDebug("[Mock] Audio playback cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Mock] Audio playback error");
        }
    }

    private void UpdateState(ScannerState newState)
    {
        _state = newState;
        OnStateChanged?.Invoke(_state);
    }
}

/// <summary>
/// Configuration object for a mock scenario.
/// </summary>
public class ScenarioConfig
{
    /// <summary>
    /// List of events in the scenario.
    /// </summary>
    public List<ScenarioEvent> Events { get; set; } = new();
}

/// <summary>
/// Defines a single event in a mock scenario.
/// </summary>
public class ScenarioEvent
{
    /// <summary>
    /// Time offset in seconds from the start of the scenario.
    /// </summary>
    public double Time { get; set; }
    
    /// <summary>
    /// Frequency of the event in MHz.
    /// </summary>
    public double Frequency { get; set; }
    
    /// <summary>
    /// Path to the audio file to play.
    /// </summary>
    public string? AudioFile { get; set; }
    
    /// <summary>
    /// Expected duration of the event.
    /// </summary>
    public double Duration { get; set; }
    
    /// <summary>
    /// Simulated P25 Source ID.
    /// </summary>
    public int? SourceId { get; set; }
    
    /// <summary>
    /// Simulated P25 Target ID.
    /// </summary>
    public int? TargetId { get; set; }
}
