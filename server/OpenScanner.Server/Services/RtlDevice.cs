using System.Diagnostics;
using System.Numerics;
using System.Text;
using FftSharp;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

public class RtlDevice : BackgroundService
{
    private readonly IDatabase _db;
    private readonly ILogger<RtlDevice> _logger;
    private readonly GpsService _gps;
    private readonly ToneDetector _toneDetector;

    public event Action<ScannerState>? OnStateChanged;
    public event Action<CallLog>? OnNewLog;
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
    private Process? _decoderProcess;
    private CancellationTokenSource? _activityTimeoutCts;
    private long _recordingStartTime;
    private int? _currentSourceID;
    private int? _currentTargetID;
    private DateTime _lastActivityReset = DateTime.MinValue;

    // Pre-roll buffer to capture start of transmissions
    private readonly LinkedList<byte[]> _preRollBuffer = new();
    private int _preRollSize = 0;
    private const int MaxPreRollBytes = 48000 * 2 * 2; // 2 seconds (48k, 16bit)
    private readonly object _audioLock = new();

    public RtlDevice(IDatabase db, ILogger<RtlDevice> logger, GpsService gps, ToneDetector toneDetector)
    {
        _db = db;
        _logger = logger;
        _gps = gps;
        _toneDetector = toneDetector;
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

    public ScannerState GetState() => _state;

    public void ReloadChannels()
    {
        Task.Run(async () => {
            _channels = (await _db.GetAllChannelsAsync()).ToList();
            _logger.LogInformation($"Loaded {_channels.Count} channels.");
        });
    }

    public void SetSquelch(double db)
    {
        UpdateState(_state with { Squelch = db });
        _logger.LogInformation($"Squelch set to {db}dB");
    }

    public void Start()
    {
        if (_state.Status != "IDLE") return;
        _manualOverride = false;
        _logger.LogInformation("Starting Scanner...");
        StartScanning();
    }

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

    public void ResumeScan()
    {
        if (!_manualOverride) return;
        _manualOverride = false;
        
        StopDecoding();
        _recordingLockoutUntil = DateTime.UtcNow.AddSeconds(3);
        UpdateState(_state with { ManualHoldFrequency = null });
        
        // Small delay to let hardware settle
        Task.Delay(500).ContinueWith(_ => StartScanning());
    }

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
        try { _scannerProcess?.Kill(true); } catch { }
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
            } catch {}
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

                // Rate limit FFT updates (approx 15Hz)
                if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds < 60) continue;
                lastUpdate = DateTime.UtcNow;

                // Warm-up: Skip first 500ms of data to let hardware settle
                if ((DateTime.UtcNow - _scanStartTime).TotalMilliseconds < 500) continue;

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
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Error in scan loop"); }
        finally
        {
            try { _scannerProcess?.Kill(true); } catch { }
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
                    if (_channelHits[channel.Frequency] >= 3 && (bestChannel == null || db > maxDetectedDb))
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
            RestartSessionTimeout(5000); // 5s to hear something (sync up)
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
        try 
        {
            if (_decoderProcess != null && !_decoderProcess.HasExited)
            {
                // Try to kill the whole process group if possible, or just the process
                _decoderProcess.Kill(true); 
            }
        } catch { }
        _decoderProcess = null;
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
        
        string rtlMode = "fm";
        string dsdArgs = "-f1"; // Default P25
        int captureRate = 48000;
        int outputRate = 48000;
        int dsdOutputRate = 48000; // Default to 48k for all modes
        string ffmpegFilter = "";

        string mode = channel.Mode?.ToUpper() ?? "P25";
        
        if (mode == "AM") {
            rtlMode = "am";
            dsdArgs = "-A"; // Force analog
            dsdOutputRate = 48000; // Analog pass-through is 48k
        } else if (mode == "FM" || mode == "NFM") {
            rtlMode = "fm";
            dsdArgs = "-A"; // Force analog
            dsdOutputRate = 48000; // Analog pass-through is 48k
        } else if (mode == "WFM") {
            rtlMode = "wbfm";
            dsdArgs = "-A"; // Force analog
            captureRate = 170000; // WFM needs higher bandwidth
        } else if (mode == "P25") {
            rtlMode = "fm";
            dsdArgs = "-f1"; // P25 Phase 1
            dsdOutputRate = 8000; // User reported "Fast" playback with 48k, so output must be 8k
            ffmpegFilter = ""; 
        } else {
            // Unknown, try auto
            rtlMode = "fm";
            dsdArgs = "-fa";
        }

        // If a specific CTCSS tone is requested in the channel config, we could pass it here,
        // but dsd-fme -A will find any tone and report it.

        // Always resample to 48k for dsd-fme consistency
        string cmd;
        if (mode == "WFM")
        {
            // WFM: Bypass dsd-fme (it doesn't handle WFM well) and output raw audio from rtl_fm
            // Output is already 48k (outputRate)
            cmd = $"rtl_fm -f {channel.Frequency}M -s {captureRate} -r {outputRate} -g 45 -p 0 -M {rtlMode} -";
        }
        else
        {
            // DSD-FME defaults: Input 48k (from rtl_fm), Output 8k (Digital) or 48k (Analog pass).
            // We match FFmpeg input rate (-ar) to the expected DSD-FME output.
            // Output rate is always 48000 to match WFM and Client expectation.
            // Re-added -s {outputRate} because DSD-FME needs it for raw input
            cmd = $"rtl_fm -f {channel.Frequency}M -s {captureRate} -r {outputRate} -g 45 -p 0 -M {rtlMode} - | /usr/local/bin/dsd-fme {dsdArgs} -i - -o - -s {outputRate} | /usr/bin/ffmpeg -f s16le -ar {dsdOutputRate} -ac 1 -i - {ffmpegFilter} -f s16le -ar {outputRate} -ac 1 -fflags nobuffer -flags low_delay - -loglevel quiet";
        }

        var psi = new ProcessStartInfo("sh", $"-c \"{cmd}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        _logger.LogInformation($"Decoder starting ({mode}): {cmd}");

        // Delay slightly
        Task.Delay(300).ContinueWith(_ => 
        {
            if (_state.Status == "IDLE") return; // Aborted

            try
            {
                _decoderProcess = Process.Start(psi);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start decoder pipeline");
                return;
            }

            if (_decoderProcess == null) return;

            // WFM Keep-Alive
            if (mode == "WFM")
            {
                var kaToken = _decodeCts.Token;
                Task.Run(async () => {
                     try {
                        while (!kaToken.IsCancellationRequested) {
                            HandleActivity(channel, null, null, null);
                            
                            // If we are scanning (not holding), trigger once then let it timeout (5s)
                            // This prevents getting stuck on a WFM channel forever.
                            if (!_manualOverride) break;

                            await Task.Delay(2000, kaToken);
                        }
                     } catch {}
                }, kaToken);
            }

            // Handle Audio (Stdout)
            var token = _decodeCts.Token;
            Task.Run(async () => 
            {
                var readBuffer = new byte[2048];
                var sendBuffer = new List<byte>(4096);
                var stream = _decoderProcess.StandardOutput.BaseStream;
                var lastSend = DateTime.UtcNow;
                bool hadData = false;

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        // Use a timeout to flush small buffers
                        var readTask = stream.ReadAsync(readBuffer, 0, readBuffer.Length, token);
                        var delayTask = Task.Delay(200, token); // 200ms flush timeout
                        
                        var completedTask = await Task.WhenAny(readTask, delayTask);
                        
                        if (completedTask == readTask)
                        {
                            int read = await readTask;
                            if (read == 0) break;
                            if (!hadData) { _logger.LogInformation("Decoder: Received first audio bytes"); hadData = true; }
                            for (int i = 0; i < read; i++) sendBuffer.Add(readBuffer[i]);
                        }

                        // Send if we have enough or if it's been too long
                        // Increased threshold to 16384 bytes (~170ms) to reduce WebSocket overhead and crackling
                        if (sendBuffer.Count >= 16384 || (sendBuffer.Count > 0 && (DateTime.UtcNow - lastSend).TotalMilliseconds > 200))
                        {
                            var chunk = sendBuffer.ToArray();
                            sendBuffer.Clear();
                            
                            // Analyze for Fire Tone Outs
                            _toneDetector.ProcessAudio(chunk);

                            lock (_audioLock)
                            {
                                // Strict attribution check: If we switched channels, drop this audio packet
                                if (_state.CurrentChannel == null || Math.Abs(_state.CurrentChannel.Frequency - channel.Frequency) > 0.001) continue;

                                OnAudio?.Invoke(chunk);
                                lastSend = DateTime.UtcNow;

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
                                    ResetActivityTimeout(); // Keep recording alive while audio flows
                                }
                            }
                        }
                    }

                    // Check if process died unexpectedly
                    if (!token.IsCancellationRequested && _decoderProcess != null && _decoderProcess.HasExited && _decoderProcess.ExitCode != 0)
                    {
                        _logger.LogError($"Decoder pipeline exited with code {_decoderProcess.ExitCode}");
                        if (_state.Status == "RECEIVING") UpdateState(_state with { Status = "IDLE" });
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger.LogError(ex, "Decoder audio read error");
                }
            }, token);

            // Handle Metadata (Stderr)
            Task.Run(async () => 
            {
                try
                {
                    while (!token.IsCancellationRequested && !_decoderProcess.HasExited)
                    {
                        var line = await _decoderProcess.StandardError.ReadLineAsync(token);
                        if (line != null)
                        {
                            // Log all decoder output for debugging the "missing audio" issue
                            _logger.LogDebug($"Decoder: {line}");

                            // Expand detection to capture more frame types and analog activity
                            // STRICTER FILTER: Ignore "Sync:" and "P25" to avoid locking on Control Channels (TSBK)
                            // We only want to lock on Voice frames (LDU/VDU) or Analog indicators.
                            bool isActivity = 
                                line.Contains("Voice") || 
                                line.Contains("LDU") || line.Contains("VDU") || // P25 Voice Frames
                                line.Contains("HDU") || // P25 Header Data Unit (Start of Call)
                                line.Contains("TDU") || // P25 Terminator
                                (line.Contains("P25") && !line.Contains("TSBK")) || // Allow P25 sync but not control channel
                                line.Contains("CTCSS") || line.Contains("DCS") || line.Contains("ANALOG");

                            if (isActivity)
                            {
                                int? src = null;
                                int? tgt = null;
                                string? tone = null;

                                // Parse Source/Target if present in line
                                if (line.Contains("Source:"))
                                {
                                    var parts = line.Split("Source:");
                                    if (parts.Length > 1) {
                                        var val = parts[1].Trim().Split(' ')[0];
                                        if (int.TryParse(val, out var s)) src = s;
                                    }
                                }
                                else if (line.Contains("Src:"))
                                {
                                     var parts = line.Split("Src:");
                                    if (parts.Length > 1) {
                                        var val = parts[1].Trim().Split(' ')[0];
                                        if (int.TryParse(val, out var s)) src = s;
                                    }
                                }

                                if (line.Contains("Target:"))
                                {
                                    var parts = line.Split("Target:");
                                    if (parts.Length > 1) {
                                        var val = parts[1].Trim().Split(' ')[0];
                                        if (int.TryParse(val, out var t)) tgt = t;
                                    }
                                }
                                else if (line.Contains("Tgt:"))
                                {
                                     var parts = line.Split("Tgt:");
                                    if (parts.Length > 1) {
                                        var val = parts[1].Trim().Split(' ')[0];
                                        if (int.TryParse(val, out var t)) tgt = t;
                                    }
                                }

                                if (line.Contains("CTCSS:"))
                                {
                                    var parts = line.Split("CTCSS:");
                                    if (parts.Length > 1) tone = parts[1].Trim().Split(' ')[0] + " Hz";
                                }
                                else if (line.Contains("DCS:"))
                                {
                                    var parts = line.Split("DCS:");
                                    if (parts.Length > 1) tone = "D" + parts[1].Trim().Split(' ')[0];
                                }

                                HandleActivity(channel, src, tgt, tone);
                            }
                        }
                    }
                }
                catch {}
            }, token);

        });
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
                     try { File.Delete(recordingPath); } catch {}
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
                             try { File.Delete(recordingPath); } catch {}
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
                                 try { File.Delete(recordingPath); } catch { }
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