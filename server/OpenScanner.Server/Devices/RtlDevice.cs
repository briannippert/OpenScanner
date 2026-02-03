using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using OpenScanner.Server.Models;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Services;

namespace OpenScanner.Server.Devices;

/// <summary>
/// Service that interfaces with RTL-SDR hardware to scan, receive, and process radio signals.
/// </summary>
public class RtlDevice : BackgroundService, IRadioSource
{
    private readonly IDatabase _db;
    private readonly ILogger<RtlDevice> _logger;
    private readonly GpsService _gps;
    private readonly ToneDetector _toneDetector;
    private readonly IDecoderFactory _decoderFactory;
    private readonly ITranscriptionService _transcriptionService;
    private readonly IRecordingService _recordingService;
    private readonly IChannelService _channelService;

    /// <inheritdoc />
    public event Action<ScannerState>? OnStateChanged;
    
    /// <inheritdoc />
    public event Action<CallLog>? OnNewLog;
    
    /// <inheritdoc />
    public event Action<byte[]>? OnAudio;

    private ScannerState _state = new ScannerState("IDLE", 0);
    private bool _manualOverride = false;
    
    private string? _lastDetectedTone;

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
    private string? _iqDumpPath;
    
    // Audio buffering
    private readonly LinkedList<byte[]> _preRollBuffer = new();
    private const int PreRollMaxBytes = 96000 * 2; // ~2 seconds at 48kHz 16-bit

    private readonly ConcurrentDictionary<double, DateTime> _channelLockouts = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RtlDevice"/> class.
    /// </summary>
    public RtlDevice(
        IDatabase db, 
        ILogger<RtlDevice> logger, 
        GpsService gps, 
        ToneDetector toneDetector, 
        IDecoderFactory decoderFactory, 
        ITranscriptionService transcriptionService, 
        IRecordingService recordingService,
        IChannelService channelService)
    {
        _db = db;
        _logger = logger;
        _gps = gps;
        _toneDetector = toneDetector;
        _decoderFactory = decoderFactory;
        _transcriptionService = transcriptionService;
        _recordingService = recordingService;
        _channelService = channelService;
        _state = new ScannerState("IDLE", 0);
        
        _gps.OnGpsUpdate += (data) => 
        {
            UpdateState(_state with { Gps = data });
            _channelService.CheckGeoRefresh(data.Lat, data.Lon);
        };

        _toneDetector.OnToneDetected += (tone) => {
            _lastDetectedTone = tone.Name;
            UpdateState(_state with { LastDetectedTone = tone.Name });
        };

        _recordingService.OnNewLog += (log) => {
            OnNewLog?.Invoke(log);
        };
        
        _channelService.ReloadChannels();
    }

    /// <inheritdoc />
    public ScannerState GetState() => _state;

    /// <inheritdoc />
    public void ReloadChannels()
    {
        _channelService.ReloadChannels();
    }

    /// <inheritdoc />
    public void SetSquelch(double db)
    {
        UpdateState(_state with { Squelch = db });
        _logger.LogInformation($"Squelch set to {db}dB");
    }

    /// <inheritdoc />
    public void Start()
    {
        if (_state.Status != "IDLE") return;
        _manualOverride = false;
        _logger.LogInformation("Starting Scanner...");
        StartScanning();
    }

    /// <inheritdoc />
    public void Stop()
    {
        StopScanning();
        StopDecoding();
        UpdateState(_state with { 
            Status = "IDLE", 
            CurrentFrequency = null, 
            CurrentChannel = null, 
            SignalStrength = 0,
            ManualHoldFrequency = null
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
            ManualHoldFrequency = null
        });
        
        // Small delay to let hardware settle before restarting the scan
        Task.Delay(250).ContinueWith(_ => StartScanning());
    }

    /// <inheritdoc />
    public void StartDumping(string label)
    {
        var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "../../data/samples");
        if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
        
        _iqDumpPath = Path.Combine(dataDir, $"{label}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.iq");
        _iqDumpStream = new FileStream(_iqDumpPath, FileMode.Create);
        _logger.LogInformation($"Started IQ dumping to {_iqDumpPath}");
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
    /// Executes the background service.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1000, stoppingToken);
        Start();
    }

    private void UpdateState(ScannerState newState)
    {
        _state = newState;
        OnStateChanged?.Invoke(_state);
    }

    private void StopScanning()
    {
        _scanCts?.Cancel();
        try { _scannerProcess?.Kill(true); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to kill scanner process"); }
        _scannerProcess = null;
    }

    private void StartScanning()
    {
        if (_channelService.Channels.Count == 0 || _manualOverride || _state.Status == "RECEIVING") return;

        _channelHits.Clear();
        _scanStartTime = DateTime.UtcNow;
        UpdateState(_state with { Status = "SCANNING", CurrentFrequency = null, CurrentChannel = null, SignalStrength = 0, ManualHoldFrequency = null });

        var min = _channelService.Channels.Min(c => c.Frequency);
        var max = _channelService.Channels.Max(c => c.Frequency);
        var center = (min + max) / 2.0;
        var rate = 2048000;

        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        Task.Run(() => RunScannerLoop(center, rate, token), token);
    }

    private async Task RunScannerLoop(double centerFreqMhz, int sampleRate, CancellationToken token)
    {
        var centerHz = (long)(centerFreqMhz * 1000000);
        var args = $"-f {centerHz} -s {sampleRate} -g 40 -" ;

        _logger.LogInformation($"Starting Fast Scan: {centerFreqMhz} MHz");

        var psi = new ProcessStartInfo("rtl_sdr", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try 
        {
            _scannerProcess = Process.Start(psi);
            // Assume connected if process starts, confirm via stderr later
            UpdateState(_state with { IsHardwareConnected = true });
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to start rtl_sdr");
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
                    
                    if (line.Contains("Found") || line.Contains("Using device")) 
                        UpdateState(_state with { IsHardwareConnected = true });
                    if (line.Contains("No supported devices"))
                    {
                        UpdateState(_state with { IsHardwareConnected = false });
                        _scanCts?.Cancel();
                    }
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

        try
        {
            while (!token.IsCancellationRequested)
            {
                // Watchdog: If ReadAsync hangs for > 3 seconds, we assume hardware stall
                var readTask = baseStream.ReadAsync(buffer, 0, buffer.Length, token);
                var timeoutTask = Task.Delay(3000, token);

                var completedTask = await Task.WhenAny(readTask, timeoutTask);
                if (completedTask == timeoutTask)
                {
                    _logger.LogWarning("Scanner hardware stalled (Read Timeout). Restarting...");
                    break; 
                }

                var bytesRead = await readTask;
                if (bytesRead == 0) break;

                // Rate limit FFT updates (approx 50Hz)
                if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds < 20) continue;
                lastUpdate = DateTime.UtcNow;

                // Warm-up: Skip first 500ms of data to let hardware settle
                if ((DateTime.UtcNow - _scanStartTime).TotalMilliseconds < 500) continue;

                if (_iqDumpStream != null)
                {
                    await _iqDumpStream.WriteAsync(buffer, 0, bytesRead, token);
                }

                ProcessSamples(buffer, bytesRead, centerFreqMhz, sampleRate);
            }

            // Loop exited
            if (_scannerProcess != null && _scannerProcess.HasExited && _scannerProcess.ExitCode != 0)
            {
                _logger.LogError($"Scanner process exited unexpectedly with code {_scannerProcess.ExitCode}");
                UpdateState(_state with { IsHardwareConnected = false, Status = "IDLE" });
            }
            else if (_state.Status == "SCANNING")
            {
                // Clean exit or stopped
                UpdateState(_state with { Status = "IDLE" });
            }
        }
        catch (OperationCanceledException) 
        {
            _logger.LogDebug("Scanner loop cancelled");
        }
        catch (Exception ex) { _logger.LogError(ex, "Error in scan loop"); }
        finally
        {
            try { _scannerProcess?.Kill(true); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to kill scanner process in finally"); }
        }
    }

    private void ProcessSamples(byte[] buffer, int length, double centerFreq, int sampleRate)
    {
        if (length < 1024) return;
        
        // Use last 512 samples (1024 bytes) for lowest latency
        int fftSize = 256;
        int bytesNeeded = fftSize * 2;
        int offset = length - bytesNeeded;
        if (offset < 0) return;

        var complexSamples = new Complex[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            // Normalize 0-255 to -1.0 to 1.0
            double iSample = (buffer[offset + i * 2] - 127.5) / 127.5;
            double qSample = (buffer[offset + i * 2 + 1] - 127.5) / 127.5;
            
            // Hanning Window
            double window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (fftSize - 1)));
            complexSamples[i] = new Complex(iSample * window, qSample * window);
        }

        // FFT
        FftSharp.FFT.Forward(complexSamples);
        
        // Power in dB
        var fftDb = new double[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            var power = complexSamples[i].Magnitude * complexSamples[i].Magnitude;
            fftDb[i] = 10 * Math.Log10(power + 1e-9);
        }

        // FFT Shift (Swap halves)
        var shiftedDb = new double[fftSize];
        Array.Copy(fftDb, fftSize / 2, shiftedDb, 0, fftSize / 2);
        Array.Copy(fftDb, 0, shiftedDb, fftSize / 2, fftSize / 2);

        // Normalize (Empirical)
        for(int i=0; i<fftSize; i++) shiftedDb[i] -= 20;

        // Map to SpectrumPoint objects
        var spectrumPoints = new SpectrumPoint[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            double freqOffset = (i - fftSize / 2.0) * (sampleRate / (double)fftSize);
            spectrumPoints[i] = new SpectrumPoint((centerFreq * 1000000 + freqOffset) / 1000000, shiftedDb[i]);
        }

        UpdateState(_state with { RfSpectrum = spectrumPoints });

        // Check Channels
        CheckChannels(shiftedDb, centerFreq, sampleRate, fftSize);
    }

    private void CheckChannels(double[] fftDb, double centerFreq, int sampleRate, int fftSize)
    {
        Channel? bestChannel = null;
        double maxDetectedDb = -100;
        
        // Calculate average noise floor
        double sum = 0;
        for (int i = 0; i < fftSize; i++) sum += fftDb[i];
        double avgNoise = sum / fftSize;

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

        foreach (var channel in _channelService.Channels)
        {
            if (channel.Avoid) continue;

            // Check for channel lockout
            if (_channelLockouts.TryGetValue(channel.Frequency, out var lockoutUntil) && DateTime.UtcNow < lockoutUntil)
            {
                _logger.LogInformation($"--> Channel {channel.AlphaTag} ({channel.Frequency} MHz) is currently locked out until {lockoutUntil:HH:mm:ss}. Skipping.");
                continue;
            }

            double freqDiff = (channel.Frequency - centerFreq) * 1000000;
            int binIndex = (int)((freqDiff / sampleRate) * fftSize + fftSize / 2.0);

            if (binIndex >= 0 && binIndex < fftSize)
            {
                // DC Filter: Ignore center 3 bins
                int centerBin = fftSize / 2;
                if (binIndex >= centerBin - 1 && binIndex <= centerBin + 1) continue;

                double db = fftDb[binIndex];
                if (db > maxDetectedDb) maxDetectedDb = db;

                // SNR Requirement: Signal must be at least 15dB above average noise
                // AND above absolute threshold
                double snr = db - avgNoise;
                double threshold = _state.Squelch ?? -55;

                if (snr > 15 && db > threshold)
                {
                    _channelHits[channel.Frequency] = _channelHits.GetValueOrDefault(channel.Frequency, 0) + 1;
                    
                    // Instant lock for strong signals (>20dB SNR), otherwise require 3 hits
                    int hitsNeeded = snr > 20 ? 1 : 3;
                    
                    if (_channelHits[channel.Frequency] >= hitsNeeded && (bestChannel == null || db > maxDetectedDb))
                    {
                        bestChannel = channel;
                    }
                }
                else
                {
                    _channelHits[channel.Frequency] = 0;
                }
            }
        }

        // Map dB to 0-100% (Assumes -60dB noise floor, 0dB max)
        double strength = Math.Clamp((maxDetectedDb + 60) / 60.0 * 100, 0, 100);
        UpdateState(_state with { CurrentSignalDb = maxDetectedDb, SignalStrength = strength });

        if (bestChannel != null)
        {
            _logger.LogInformation($"Detected carrier on {bestChannel.AlphaTag} (SNR: {(maxDetectedDb - avgNoise):F1}dB)");
            StopScanning();
            _recordingLockoutUntil = DateTime.UtcNow.AddSeconds(3);
            LockOn(bestChannel);
        }
    }

    private void LockOn(Channel channel)
    {
        _logger.LogInformation($"Locked on to {channel.AlphaTag} ({channel.Frequency} MHz)");

        UpdateState(_state with 
        { 
            Status = _manualOverride ? "MONITORING" : "RECEIVING",
            CurrentFrequency = channel.Frequency,
            CurrentChannel = channel,
            IsAudioStreaming = true
        });

        StartDecoding(channel);

        // Safety timeout
        if (!_manualOverride)
        {
            RestartSessionTimeout(10000); // 10s to hear something (sync up)
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
                if (_recordingService.IsRecording) return;

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
            _recordingService.StopRecording(_state.CurrentChannel, _lastDetectedTone);
        }
        
        if (_currentDecoder != null)
        {
            _currentDecoder.Stop();
            _currentDecoder = null;
        }
        
        // Clear pre-roll when we stop decoding/move on? 
        // Or keep it? Usually better to clear or let it ring out.
        lock(_preRollBuffer) _preRollBuffer.Clear();
    }

    private void StartDecoding(Channel channel)
    {
        StopDecoding();
        
        _decodeCts = new CancellationTokenSource();
        var token = _decodeCts.Token;

        try 
        {
            _currentDecoder = _decoderFactory.GetDecoder(channel.Mode);
            
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

                // Analyze for Fire Tone Outs
                _toneDetector.ProcessAudio(chunk);
                OnAudio?.Invoke(chunk);

                _recordingService.ProcessAudio(chunk);
                
                if (_recordingService.IsRecording)
                {
                    ResetActivityTimeout(); 
                }
            };

            _currentDecoder.OnActivity += (src, tgt, tone) => HandleActivity(channel, src, tgt, tone);
            _currentDecoder.OnMetadata += (line) => _logger.LogDebug($"Decoder: {line}");

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

        _activityTimeoutCts?.Cancel();
        _activityTimeoutCts = new CancellationTokenSource();
        
        if (!_manualOverride)
        {
            RestartSessionTimeout(5000); // 5s hang time
        }

        Task.Delay(2000, _activityTimeoutCts.Token).ContinueWith(t => 
        {
            if (!t.IsCanceled)
            {
                if (_state.CurrentChannel != null)
                {
                    _recordingService.StopRecording(_state.CurrentChannel, _lastDetectedTone);
                }

                if (_manualOverride) UpdateState(_state with { Status = "MONITORING" });
            }
        });
    }

    private void HandleActivity(Channel originChannel, int? src = null, int? tgt = null, string? tone = null)
    {
        // STRICT ATTRIBUTION: Ignore activity if we have moved to a different channel
        if (_state.CurrentChannel == null || Math.Abs(_state.CurrentChannel.Frequency - originChannel.Frequency) > 0.001) return;

        // Valid Activity detected
        if (_state.Status != "RECEIVING" || 
            (src.HasValue && _state.SourceID != src) || 
            (tone != null && _state.CurrentTone != tone))
        {
            UpdateState(_state with { 
                Status = "RECEIVING", 
                SourceID = src ?? _state.SourceID, 
                TargetID = tgt ?? _state.TargetID, 
                CurrentTone = tone ?? _state.CurrentTone,
                LastTranscription = null 
            });
        }
        
        // Start recording if not already started
        if (!_recordingService.IsRecording)
        {
            _recordingService.StartRecording(originChannel, src, tgt, _preRollBuffer);
        }

        ResetActivityTimeout();
    }
}
