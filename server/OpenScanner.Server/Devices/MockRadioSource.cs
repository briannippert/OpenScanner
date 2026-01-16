using System.Diagnostics;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;

namespace OpenScanner.Server.Devices;

public class MockRadioSource : BackgroundService, IRadioSource
{
    private readonly ILogger<MockRadioSource> _logger;
    private readonly IDatabase _db;
    private readonly GpsService _gps;
    private readonly ToneDetector _toneDetector;
    private readonly IDecoderFactory _decoderFactory;
    private readonly ITranscriptionService _transcriptionService;
    private readonly IRecordingService _recordingService;
    private readonly IChannelService _channelService;

    public event Action<ScannerState>? OnStateChanged;
    public event Action<CallLog>? OnNewLog;
    public event Action<byte[]>? OnAudio;

    private ScannerState _state = new ScannerState("IDLE", 0);
    private List<ScenarioEvent> _scenarioEvents = new();
    private DateTime _startTime;
    private bool _isRunning;
    private bool _manualOverride;
    private CancellationTokenSource? _currentTaskCts;

    // Audio buffering
    private readonly LinkedList<byte[]> _preRollBuffer = new();
    private const int PreRollMaxBytes = 96000 * 2; 

    public MockRadioSource(
        ILogger<MockRadioSource> logger,
        IDatabase db,
        GpsService gps,
        ToneDetector toneDetector,
        IDecoderFactory decoderFactory,
        ITranscriptionService transcriptionService,
        IRecordingService recordingService,
        IChannelService channelService)
    {
        _logger = logger;
        _db = db;
        _gps = gps;
        _toneDetector = toneDetector;
        _decoderFactory = decoderFactory;
        _transcriptionService = transcriptionService;
        _recordingService = recordingService;
        _channelService = channelService;

        _state = new ScannerState("IDLE", 0);

        _recordingService.OnNewLog += (log) => OnNewLog?.Invoke(log);
        
        // Try to load default scenario
        var scenarioPath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "scenario.json");
        if (File.Exists(scenarioPath))
        {
             try 
             {
                 var json = File.ReadAllText(scenarioPath);
                 var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                 
                 // Try array first
                 try 
                 {
                     var events = System.Text.Json.JsonSerializer.Deserialize<List<ScenarioEvent>>(json, options);
                     if (events != null) _scenarioEvents = events;
                 }
                 catch (System.Text.Json.JsonException)
                 {
                     // Try object wrapper
                     var wrapper = System.Text.Json.JsonSerializer.Deserialize<ScenarioWrapper>(json, options);
                     if (wrapper?.Events != null) _scenarioEvents = wrapper.Events;
                 }
             }
             catch (Exception ex)
             {
                 _logger.LogWarning(ex, "Failed to load default scenario");
             }
        }
    }

    public void SetScenario(List<ScenarioEvent> events)
    {
        _scenarioEvents = events;
    }

    public ScannerState GetState() => _state;

    public void ReloadChannels()
    {
        _channelService.ReloadChannels();
    }

    public void SetSquelch(double db)
    {
        UpdateState(_state with { Squelch = db });
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        _startTime = DateTime.UtcNow;
        _manualOverride = false;
        UpdateState(_state with { Status = "SCANNING", IsHardwareConnected = true });
        
        _currentTaskCts = new CancellationTokenSource();
        Task.Run(() => SimulationLoop(_currentTaskCts.Token));
    }

    public void Stop()
    {
        _isRunning = false;
        _currentTaskCts?.Cancel();
        UpdateState(_state with { Status = "IDLE", IsHardwareConnected = true });
    }

    public void HoldFrequency(double freq)
    {
        _manualOverride = true;
        UpdateState(_state with { ManualHoldFrequency = freq, Status = "MONITORING", CurrentFrequency = freq });
    }

    public void ResumeScan()
    {
        _manualOverride = false;
        UpdateState(_state with { Status = "SCANNING", ManualHoldFrequency = null });
    }

    public void StartDumping(string label) { }
    public void StopDumping() { }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Auto-start the mock simulation on startup
        Start();
        await Task.CompletedTask;
    }

    private async Task SimulationLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _isRunning)
            {
                var elapsed = (DateTime.UtcNow - _startTime).TotalSeconds;

                // Loop scenario if we passed the end (max time + 10s buffer)
                var maxTime = _scenarioEvents.Any() ? _scenarioEvents.Max(e => e.Time + e.Duration) : 0;
                if (maxTime > 0 && elapsed > maxTime + 10)
                {
                    _startTime = DateTime.UtcNow;
                    _logger.LogInformation("[MockRadioSource] Scenario loop resetting...");
                    elapsed = 0;
                }

                // Find active event
                var activeEvent = _scenarioEvents.FirstOrDefault(e => 
                    elapsed >= e.Time && elapsed < (e.Time + e.Duration));

                if (activeEvent != null)
                {
                    // Check if we should hear it
                    bool canHear = false;
                    if (_manualOverride)
                    {
                        if (Math.Abs((_state.ManualHoldFrequency ?? 0) - activeEvent.Frequency) < 0.001) canHear = true;
                    }
                    else
                    {
                        // In scanning mode, we "stop" on it
                        canHear = true;
                    }

                    if (canHear)
                    {
                        if (_state.Status != "RECEIVING" && _state.Status != "MONITORING")
                        {
                            // Lock onto it
                            var channel = _channelService.Channels.FirstOrDefault(c => Math.Abs(c.Frequency - activeEvent.Frequency) < 0.001);
                            
                            // Check if avoided
                            if (channel != null && channel.Avoid && !_manualOverride)
                            {
                                canHear = false;
                            }
                            else
                            {
                                if (channel == null)
                                {
                                    // Mock channel if not found
                                    channel = new Channel(activeEvent.Frequency, "Mock", "Mock Channel", activeEvent.DecoderType ?? "FM", "FM");
                                }

                                UpdateState(_state with { 
                                    Status = _manualOverride ? "MONITORING" : "RECEIVING",
                                    CurrentFrequency = activeEvent.Frequency,
                                    CurrentChannel = channel,
                                    SourceID = activeEvent.SourceId,
                                    TargetID = activeEvent.TargetId
                                });
                                
                                // Start recording via service
                                if (!_recordingService.IsRecording)
                                {
                                    _recordingService.StartRecording(channel, activeEvent.SourceId, activeEvent.TargetId, _preRollBuffer);
                                }
                            }
                        }

                        if (canHear)
                        {
                            // Simulate Audio
                            if (!string.IsNullOrEmpty(activeEvent.AudioFile))
                            {
                                await PlayAudioFile(activeEvent, token);
                            }
                            else
                            {
                                _logger.LogWarning($"[MockRadioSource] Event active but AudioFile is null/empty. Check scenario JSON mapping. Frequency: {activeEvent.Frequency}");
                                // Generate silence or noise
                                await Task.Delay(100, token);
                            }
                        }
                    }
                    else
                    {
                        if (!_manualOverride && _state.Status == "RECEIVING")
                        {
                            // Lost signal
                             UpdateState(_state with { Status = "SCANNING", CurrentChannel = null });
                             if (_state.CurrentChannel != null) _recordingService.StopRecording(_state.CurrentChannel, null);
                        }
                    }
                }
                else
                {
                    // No active event
                    if (!_manualOverride && _state.Status == "RECEIVING")
                    {
                        UpdateState(_state with { Status = "SCANNING", CurrentChannel = null });
                        if (_state.CurrentChannel != null) _recordingService.StopRecording(_state.CurrentChannel, null);
                    }
                }

                await Task.Delay(100, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Simulation loop error");
        }
    }

    private async Task PlayAudioFile(ScenarioEvent evt, CancellationToken token)
    {
        string filename = evt.AudioFile!;
        
        // Robust path resolution
        var searchPaths = new List<string>
        {
            Path.Combine(Directory.GetCurrentDirectory(), "TestData", filename),
            Path.Combine(Directory.GetCurrentDirectory(), "bin", "Debug", "net10.0", "TestData", filename),
            Path.Combine(AppContext.BaseDirectory, "TestData", filename),
            // Look relative to the server project if running from root
            Path.Combine(Directory.GetCurrentDirectory(), "server", "OpenScanner.Server", "TestData", filename)
        };
        
        string? path = searchPaths.FirstOrDefault(p => File.Exists(p));
        
        // Final fallback: try filename directly
        if (path == null && File.Exists(filename)) path = filename;

        if (path != null)
        {
            _logger.LogInformation($"[MockRadioSource] SUCCESS: Playing audio file from: {path} (Decoder: {evt.DecoderType ?? "None"})");

            if (evt.DecoderType?.ToUpper() == "P25")
            {
                // Use Real Decoder
                var decoder = _decoderFactory.GetDecoder("P25");
                
                // Use ffmpeg with -re (read at native rate) to simulate real-time input.
                // This prevents the decoder from running too fast and flooding the client.
                decoder.InputSource = $"/usr/bin/ffmpeg -re -i \"{path}\" -f s16le -ar 48000 -ac 1 -loglevel quiet -";
                
                // Hook up events
                Action<byte[]> audioHandler = (chunk) => 
                {
                    OnAudio?.Invoke(chunk);
                    _recordingService.ProcessAudio(chunk);
                    _toneDetector.ProcessAudio(chunk);
                };
                
                decoder.OnAudio += audioHandler;

                try 
                {
                    // Create dummy channel for decoder context
                    var dummyChannel = new Channel { Frequency = evt.Frequency };
                    await decoder.StartAsync(dummyChannel, token);
                }
                finally
                {
                    decoder.OnAudio -= audioHandler;
                    decoder.Stop();
                }
            }
            else
            {
                // Direct Playback (Simulated/Demodulated Audio)
                // For real fidelity, we should respect sample rate.
                // Assuming 48k 16bit mono for now as per system standard.
                var buffer = new byte[3200]; // ~33ms
                using var fs = File.OpenRead(path);
                // Skip header if wav
                if (path.EndsWith(".wav") && fs.Length > 44) fs.Position = 44;

                int bytesRead;
                long totalBytes = 0;
                while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                {
                    totalBytes += bytesRead;
                    // Log every ~100 chunks to avoid spam, but confirm activity
                    if (totalBytes % (3200 * 50) == 0) 
                    {
                        _logger.LogInformation($"[MockRadioSource] Streaming audio... Total: {totalBytes} bytes");
                    }

                    var chunk = new byte[bytesRead];
                    Array.Copy(buffer, chunk, bytesRead);
                    
                    OnAudio?.Invoke(chunk);
                    _recordingService.ProcessAudio(chunk);
                    _toneDetector.ProcessAudio(chunk);
                    
                    // Throttle
                    await Task.Delay(33, token);
                }
            }
        }
        else
        {
            _logger.LogWarning($"Audio file not found: {filename}. Searched in {string.Join(", ", searchPaths)}");
        }
    }

    private void UpdateState(ScannerState newState)
    {
        _state = newState;
        OnStateChanged?.Invoke(_state);
    }

    private class ScenarioWrapper
    {
        public List<ScenarioEvent>? Events { get; set; }
    }
}
