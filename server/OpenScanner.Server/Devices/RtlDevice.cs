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

    /// <inheritdoc />
    public event Action<ScannerState>? OnStateChanged;
    
    /// <inheritdoc />
    public event Action<CallLog>? OnNewLog;
    
    /// <inheritdoc />
    public event Action<byte[]>? OnAudio;

    private string? _currentRecordingPath;
    private ScannerState _state = new ScannerState("IDLE", 0);
    private List<Channel> _channels = new();
    private bool _manualOverride = false;
    
    private string? _lastDetectedTone;

    private (double Lat, double Lon)? _lastGeoPosition;
    private DateTime _recordingLockoutUntil = DateTime.MinValue;
    private CancellationTokenSource? _scanCts;
    private Process? _scannerProcess;
    private Dictionary<double, int> _channelHits = new();
    private DateTime _scanStartTime;
    private CancellationTokenSource? _sessionTimeoutCts;
    private FileStream? _recordingStream;
    private CancellationTokenSource? _decodeCts;
    private IDecoder? _currentDecoder;
    private CancellationTokenSource? _activityTimeoutCts;
    private long _recordingStartTime;
    private int? _currentSourceID;
    private int? _currentTargetID;
    private DateTime _lastActivityReset = DateTime.MinValue;

    private FileStream? _iqDumpStream;
    private string? _iqDumpPath;

    // Pre-roll buffer to capture start of transmissions
    private readonly LinkedList<byte[]> _preRollBuffer = new();
    private int _preRollSize = 0;
    private const int MaxPreRollBytes = 48000 * 2 * 2; // 2 seconds (48k, 16bit)
    private readonly object _audioLock = new();
    private readonly Dictionary<double, DateTime> _channelLockouts = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RtlDevice"/> class.
    /// </summary>
    public RtlDevice(IDatabase db, ILogger<RtlDevice> logger, GpsService gps, ToneDetector toneDetector, IDecoderFactory decoderFactory)
    {
        _db = db;
        _logger = logger;
        _gps = gps;
        _toneDetector = toneDetector;
        _decoderFactory = decoderFactory;
        _state = new ScannerState("IDLE", 0);
        
        _gps.OnGpsUpdate += (data) => 
        {
            UpdateState(_state with { Gps = data });
            CheckGeoRefresh(data.Lat, data.Lon);
        };

        _toneDetector.OnToneDetected += (tone) => {
            _lastDetectedTone = tone.Name;
            UpdateState(_state with { LastDetectedTone = tone.Name });
        };
        
        ReloadChannels();
    }

    private void CheckGeoRefresh(double lat, double lon)
    {
        if (!_lastGeoPosition.HasValue)
        {
            _lastGeoPosition = (lat, lon);
            RefreshGeoChannels(lat, lon);
            return;
        }

        // Only refresh if moved > 1 mile
        double dist = CalculateDistance(_lastGeoPosition.Value.Lat, _lastGeoPosition.Value.Lon, lat, lon);
        if (dist > 1.0)
        {
            _lastGeoPosition = (lat, lon);
            RefreshGeoChannels(lat, lon);
        }
    }

    private void RefreshGeoChannels(double lat, double lon)
    {
        Task.Run(async () => {
            var localChannels = (await _db.GetChannelsNearAsync(lat, lon)).ToList();
            if (localChannels.Count > 0)
            {
                _logger.LogInformation($"Geo-Sync: Found {localChannels.Count} local channels.");
                _channels = localChannels; 
            }
        });
    }

    private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        var d1 = lat1 * (Math.PI / 180.0);
        var num1 = lon1 * (Math.PI / 180.0);
        var d2 = lat2 * (Math.PI / 180.0);
        var num2 = lon2 * (Math.PI / 180.0) - num1;
        var d3 = Math.Pow(Math.Sin((d2 - d1) / 2.0), 2.0) + Math.Cos(d1) * Math.Cos(d2) * Math.Pow(Math.Sin(num2 / 2.0), 2.0);
        return 6376500.0 * (2.0 * Math.Atan2(Math.Sqrt(d3), Math.Sqrt(1.0 - d3))) * 0.000621371;
    }

    /// <inheritdoc />
    public ScannerState GetState() => _state;

    /// <inheritdoc />
    public void ReloadChannels()
    {
        Task.Run(async () => {
            _channels = (await _db.GetAllChannelsAsync()).ToList();
            _logger.LogInformation($"Loaded {_channels.Count} channels.");
        });
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
        var channel = _channels.FirstOrDefault(c => Math.Abs(c.Frequency - freq) < 0.001);
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
    public void ResumeScan()
    {
        _logger.LogInformation("Resume/Skip requested.");

        // Allow skipping if either on manual hold or receiving a transmission
        if (!_manualOverride && _state.Status != "RECEIVING") return;

        _manualOverride = false;
        
        StopDecoding();
        
        // Lock out this channel for 10 seconds to avoid re-locking
        if (_state.CurrentChannel != null)
        {
            _channelLockouts[_state.CurrentChannel.Frequency] = DateTime.UtcNow.AddSeconds(10); 
            _logger.LogInformation($"Applying 10s lockout for channel: {_state.CurrentChannel.AlphaTag}");
        }
        _recordingLockoutUntil = DateTime.UtcNow.AddSeconds(3); // Keep recording lockout as is
        
        UpdateState(_state with { ManualHoldFrequency = null });
        
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
        if (_channels.Count == 0 || _manualOverride || _state.Status == "RECEIVING") return;

        _channelHits.Clear();
        _scanStartTime = DateTime.UtcNow;
        UpdateState(_state with { Status = "SCANNING", CurrentFrequency = null, CurrentChannel = null, SignalStrength = 0 });

        var min = _channels.Min(c => c.Frequency);
        var max = _channels.Max(c => c.Frequency);
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
                var bytesRead = await baseStream.ReadAsync(buffer, 0, buffer.Length, token);
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

        foreach (var channel in _channels)
        {
            // Check for channel lockout
            if (_channelLockouts.TryGetValue(channel.Frequency, out var lockoutUntil) && DateTime.UtcNow < lockoutUntil)
            {
                _logger.LogDebug($"Channel {channel.AlphaTag} ({channel.Frequency} MHz) is locked out until {lockoutUntil:HH:mm:ss}");
                continue; // Skip this channel if it's locked out
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
                if (_recordingStream != null) return;

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
        StopRecording();
        if (_currentDecoder != null)
        {
            _currentDecoder.Stop();
            _currentDecoder = null;
        }
    }

    private void StartDecoding(Channel channel)
    {
        StopDecoding();
        
        lock (_audioLock)
        {
            _preRollBuffer.Clear();
            _preRollSize = 0;
        }

        _decodeCts = new CancellationTokenSource();
        var token = _decodeCts.Token;

        try 
        {
            _currentDecoder = _decoderFactory.GetDecoder(channel.Mode);
            
            _currentDecoder.OnAudio += (chunk) => 
            {
                // Analyze for Fire Tone Outs
                _toneDetector.ProcessAudio(chunk);
                lock (_audioLock)
                {
                    // Strict attribution check
                    if (_state.CurrentChannel == null || Math.Abs(_state.CurrentChannel.Frequency - channel.Frequency) > 0.001) return;

                    OnAudio?.Invoke(chunk);

                    // Update pre-roll
                    _preRollBuffer.AddLast(chunk);
                    _preRollSize += chunk.Length;
                    while (_preRollSize > MaxPreRollBytes)
                    {
                        var first = _preRollBuffer.First;
                        if (first != null)
                        {
                            _preRollSize -= first.Value.Length;
                            _preRollBuffer.RemoveFirst();
                        }
                    }

                    if (_recordingStream != null)
                    {
                        _recordingStream.Write(chunk, 0, chunk.Length);
                        _recordingStream.Flush();
                        ResetActivityTimeout(); 
                    }
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
                StopRecording();
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
        if (_recordingStream == null)
        {
            StartRecording(originChannel, src, tgt);
        }

        ResetActivityTimeout();
    }

    private void StartRecording(Channel originChannel, int? src = null, int? tgt = null)
    {
        // Double check strict attribution and lockout
        if (_state.CurrentChannel == null || Math.Abs(_state.CurrentChannel.Frequency - originChannel.Frequency) > 0.001) return;
        if (DateTime.UtcNow < _recordingLockoutUntil) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var filename = $"rec_{now}_{_state.CurrentChannel.Frequency}.raw";
        // Ensure data dir exists
        var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "../../data/recordings");
        if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);

        var newPath = Path.Combine(dataDir, filename);
        
        lock (_audioLock)
        {
            if (_recordingStream != null) return;

            _currentRecordingPath = newPath;
            _recordingStartTime = now;
            _currentSourceID = src;
            _currentTargetID = tgt;

            try 
            {
                _recordingStream = new FileStream(_currentRecordingPath, FileMode.Create);
                _logger.LogInformation($"Starting recording: {filename}");

                // Flush pre-roll buffer
                foreach (var chunk in _preRollBuffer)
                {
                    _recordingStream.Write(chunk, 0, chunk.Length);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create recording file");
            }
        }
    }

    private void StopRecording()
    {
        string? recordingPath;
        long startTime;
        Channel? capturedChannel = _state.CurrentChannel;

                        lock (_audioLock)
                        {
                            if (_recordingStream == null) return;
                            _recordingStream.Close();
                            _recordingStream = null;
                            recordingPath = _currentRecordingPath;
                            startTime = _recordingStartTime;
                        }            var duration = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startTime) / 1000.0;
        
        if (duration >= 0.5 && recordingPath != null && File.Exists(recordingPath))
        {
             var fileInfo = new FileInfo(recordingPath);
             if (fileInfo.Length < 4096) 
             {
                 try { File.Delete(recordingPath); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete small recording file"); }
                 return;
             }

             if (capturedChannel != null)
             {
                 // Convert RAW to WAV (More robust than MP3)
                 var wavPath = Path.ChangeExtension(recordingPath, ".wav");
                 try 
                 {
                     var convertStart = new ProcessStartInfo("/usr/bin/ffmpeg")
                     {
                         RedirectStandardOutput = true,
                         RedirectStandardError = true,
                         UseShellExecute = false,
                         CreateNoWindow = true
                     };
                     // Input: Raw 48k
                     convertStart.ArgumentList.Add("-f"); convertStart.ArgumentList.Add("s16le");
                     convertStart.ArgumentList.Add("-ar"); convertStart.ArgumentList.Add("48000");
                     convertStart.ArgumentList.Add("-ac"); convertStart.ArgumentList.Add("1");
                     convertStart.ArgumentList.Add("-i"); convertStart.ArgumentList.Add(recordingPath);
                     // Output: WAV PCM
                     convertStart.ArgumentList.Add(wavPath);
                     convertStart.ArgumentList.Add("-y");

                     using (var proc = Process.Start(convertStart))
                     {
                         proc?.WaitForExit();
                     }

                     if (File.Exists(wavPath))
                     {
                         try { File.Delete(recordingPath); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete original RAW file after conversion"); }
                         recordingPath = wavPath;
                     }
                 }
                 catch (Exception ex)
                 {
                     _logger.LogError(ex, "WAV conversion failed");
                 }

                 // Run Transcription
                 string? transcription = null;
                 try 
                 {
                     transcription = TranscribeAudio(recordingPath);
                 }
                 catch (Exception ex)
                 {
                     _logger.LogError(ex, "Transcription failed");
                 }

                 if (!string.IsNullOrEmpty(transcription))
                 {
                     UpdateState(_state with { LastTranscription = transcription });
                 }

                                  var log = new CallLog(
                                      $"log_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                                      DateTime.UtcNow.ToString("o"),
                                      capturedChannel.Frequency,
                                      capturedChannel.AlphaTag,
                                      capturedChannel.Description,
                                      (_state.Gps?.Lat != 0) ? _state.Gps?.Lat : null,
                                      (_state.Gps?.Lon != 0) ? _state.Gps?.Lon : null,
                                      Path.GetFileName(recordingPath),
                                      duration,
                                      transcription,
                                      _currentSourceID,
                                      _currentTargetID,
                                      _lastDetectedTone
                                  );
                 
                                  _db.SaveTransmissionAsync(log).ContinueWith(t => {
                                      if (t.IsFaulted) _logger.LogError(t.Exception, "Failed to save transmission");
                                  });
                 
                                  _logger.LogInformation($"Saved transmission: {duration:F1}s | RID: {_currentSourceID} | Tone: {_lastDetectedTone} | Text: {transcription}");
                                  OnNewLog?.Invoke(log);
                              }
                         }
                         else if (recordingPath != null)
                         {
                             try { File.Delete(recordingPath); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete aborted recording file"); }
                         }
                         
                         _lastDetectedTone = null;
                         UpdateState(_state with { SourceID = null, TargetID = null, CurrentTone = null, LastDetectedTone = null });    
        // Only clear the path if we haven't started a new recording
        lock (_audioLock)
        {
            if (_recordingStream == null && _currentRecordingPath == recordingPath)
            {
                _currentRecordingPath = null;
            }
        }
    }

        private string? TranscribeAudio(string audioPath)
        {
            // Check setting
            var enabled = _db.GetSettingAsync("EnableTranscription").GetAwaiter().GetResult();
            if (enabled != "true") return null;
    
            // Temp file for resampling to 16k
            var tempWavPath = audioPath + ".16k.wav";            
        // Robustly find whisper.cpp root
        var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
        string? whisperRoot = null;
        
        // 1. Try absolute path first (Server specific)
        if (Directory.Exists("/home/brian/radio/OpenScanner/whisper.cpp"))
        {
            whisperRoot = "/home/brian/radio/OpenScanner/whisper.cpp";
        }
        else 
        {
            // 2. Search up
            for (int i = 0; i < 6; i++) 
            {
                if (currentDir == null) break;
                var probe = Path.Combine(currentDir.FullName, "whisper.cpp");
                if (Directory.Exists(probe))
                {
                    whisperRoot = probe;
                    break;
                }
                
                probe = Path.Combine(currentDir.FullName, "../whisper.cpp");
                 if (Directory.Exists(probe))
                {
                    whisperRoot = Path.GetFullPath(probe);
                    break;
                }

                currentDir = currentDir.Parent;
            }
        }

        if (whisperRoot == null)
        {
             var projectRoot = Directory.GetCurrentDirectory(); 
             whisperRoot = Path.GetFullPath(Path.Combine(projectRoot, "../../whisper.cpp"));
        }

        var whisperBin = Path.Combine(whisperRoot, "build/bin/whisper-cli");
        var modelPath = Path.Combine(whisperRoot, "models/ggml-small.en.bin"); 

        if (!File.Exists(whisperBin) || !File.Exists(modelPath))
        {
            _logger.LogWarning($"Whisper not found at {whisperBin} or model missing at {modelPath}. Search root was: {whisperRoot}");
            return null;
        }
        
        var convertStart = new ProcessStartInfo("/usr/bin/ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        
        if (Path.GetExtension(audioPath).Equals(".raw", StringComparison.OrdinalIgnoreCase))
        {
            convertStart.ArgumentList.Add("-f");
            convertStart.ArgumentList.Add("s16le");
            convertStart.ArgumentList.Add("-ar");
            convertStart.ArgumentList.Add("48000"); // Raw is always 48k now
            convertStart.ArgumentList.Add("-ac");
            convertStart.ArgumentList.Add("1");
        }

        convertStart.ArgumentList.Add("-i");
        convertStart.ArgumentList.Add(audioPath);
        convertStart.ArgumentList.Add("-af");
        convertStart.ArgumentList.Add("volume=15dB"); 
        convertStart.ArgumentList.Add("-ar");
        convertStart.ArgumentList.Add("16000");
        convertStart.ArgumentList.Add("-ac");
        convertStart.ArgumentList.Add("1");
        convertStart.ArgumentList.Add(tempWavPath);
        convertStart.ArgumentList.Add("-y");
        
        using (var proc = Process.Start(convertStart))
        {
            if (proc != null)
            {
                var stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                {
                    _logger.LogError($"FFmpeg conversion failed with exit code {proc.ExitCode}. Stderr: {stderr}");
                }
            }
        }

        if (!File.Exists(tempWavPath)) return null;

        // 2. Run Whisper with Radio Context
        // Prompt helps Whisper bias towards radio terminology and style
        var prompt = "Dispatch, Unit 1, 10-4, copy, over. Priority traffic, code 3 response to street intersection. Suspect description: white male, blue jeans. License plate, vehicle registration, bolo. Structure fire, medical emergency, staging area. Status check, affirmative, negative, stand by. Channel 2, tac channel, command post. Kilo, Tango, Zulu, X-ray. 10-20 location, 10-8 in service, 10-7 out of service.";
        var whisperArgs = $"-m \"{modelPath}\" -f \"{tempWavPath}\" -nt -otxt -l en --prompt \"{prompt}\""; 
        
        var whisperStart = new ProcessStartInfo(whisperBin, whisperArgs)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = whisperRoot
        };

        try
        {
            using var proc = Process.Start(whisperStart);
            if (proc != null)
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                
                if (!proc.WaitForExit(60000)) // 60s timeout
                {
                    _logger.LogWarning("Whisper timed out");
                    proc.Kill();
                }
                else
                {
                    var stderr = stderrTask.Result;
                    var stdout = stdoutTask.Result;
                    
                    if (proc.ExitCode != 0)
                    {
                        _logger.LogError($"Whisper failed with exit code {proc.ExitCode}.\nStderr: {stderr}\nStdout: {stdout}");
                    }
                    else
                    {
                        // Log debug info if no file created
                        if (!File.Exists(tempWavPath + ".txt"))
                        {
                             _logger.LogWarning($"Whisper finished but no output file.\nStderr: {stderr}\nStdout: {stdout}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running Whisper process");
        }

        File.Delete(tempWavPath); // Clean up WAV

        var txtPath = tempWavPath + ".txt";
        if (File.Exists(txtPath))
        {
            var text = File.ReadAllText(txtPath).Trim();
            File.Delete(txtPath);
            // Whisper sometimes outputs [BLANK_AUDIO] or metadata in brackets
            if (text.StartsWith("[") && text.EndsWith("]")) return null;
            return string.IsNullOrEmpty(text) ? null : text;
        }

        return null;
    }}