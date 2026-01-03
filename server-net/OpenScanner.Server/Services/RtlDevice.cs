using System.Diagnostics;
using System.Numerics;
using System.Text;
using FftSharp;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

public class RtlDevice : BackgroundService
{
    private readonly Database _db;
    private readonly ILogger<RtlDevice> _logger;
    private readonly GpsService _gps;
    private ScannerState _state;
    
    // Processes
    private Process? _scannerProcess;
    private Process? _decoderProcess;
    
    // Control
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _decodeCts;
    private bool _manualOverride = false;
    private List<Channel> _channels = new();
    
    // Audio / Recording
    private Stream? _recordingStream;
    private string? _currentRecordingPath;
    private long _recordingStartTime;
    private DateTime _recordingLockoutUntil = DateTime.MinValue;
    
    // Timers (using CancellationTokenSources for cancellation)
    private CancellationTokenSource? _sessionTimeoutCts;
    private CancellationTokenSource? _activityTimeoutCts;

    // Events
    public event Action<ScannerState>? OnStateChanged;
    public event Action<byte[]>? OnAudio;
    public event Action<CallLog>? OnNewLog;

    public RtlDevice(Database db, ILogger<RtlDevice> logger, GpsService gps)
    {
        _db = db;
        _logger = logger;
        _gps = gps;
        _state = new ScannerState("IDLE", 0);
        
        _gps.OnGpsUpdate += (data) => UpdateState(_state with { Gps = data });
        
        ReloadChannels();
    }

    public ScannerState GetState() => _state;

    public void ReloadChannels()
    {
        _channels = _db.GetAllChannels().ToList();
        _logger.LogInformation($"Loaded {_channels.Count} channels.");
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
        UpdateState(_state with { Status = "IDLE", CurrentFrequency = null, CurrentChannel = null, SignalStrength = 0 });
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
        LockOn(channel);
    }

    public void ResumeScan()
    {
        if (!_manualOverride) return;
        _manualOverride = false;
        
        StopDecoding();
        _recordingLockoutUntil = DateTime.UtcNow.AddSeconds(3);
        
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

                ProcessSamples(buffer, bytesRead, centerFreqMhz, sampleRate);
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
        double threshold = _state.Squelch ?? -55; // Use Squelch if set, else -55 default

        foreach (var channel in _channels)
        {
            double freqDiff = (channel.Frequency - centerFreq) * 1000000;
            int binIndex = (int)((freqDiff / sampleRate) * fftSize + fftSize / 2.0);

            if (binIndex >= 0 && binIndex < fftSize)
            {
                double db = fftDb[binIndex];
                if (db > maxDetectedDb) maxDetectedDb = db;

                if (db > threshold && (bestChannel == null || db > maxDetectedDb))
                {
                    bestChannel = channel;
                }
            }
        }

        UpdateState(_state with { CurrentSignalDb = maxDetectedDb });

        if (bestChannel != null)
        {
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
            RestartSessionTimeout(2000); // 2s to hear something
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
                _logger.LogInformation("Session timeout, resuming scan...");
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
        _decodeCts = new CancellationTokenSource();
        
        var cmd = $"rtl_fm -f {channel.Frequency}M -s 48000 -g 45 -p 0 -M fm - | dsd-fme -f1 -i - -o - -s 48000";
        var psi = new ProcessStartInfo("sh", $"-c \"{cmd}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

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

            // Handle Audio (Stdout)
            var token = _decodeCts.Token;
            Task.Run(async () => 
            {
                var buffer = new byte[4096];
                var stream = _decoderProcess.StandardOutput.BaseStream;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var read = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                        if (read == 0) break;

                        var chunk = new byte[read];
                        Array.Copy(buffer, chunk, read);
                        
                        // Stream to clients
                        OnAudio?.Invoke(chunk);

                        // Write to recording
                        if (_recordingStream != null)
                        {
                            await _recordingStream.WriteAsync(chunk, 0, chunk.Length, token);
                        }
                    }
                }
                catch {}
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
                            if (line.Contains("Sync:") || line.Contains("Voice") || line.Contains("P25"))
                            {
                                HandleActivity();
                            }
                            if (!line.Contains("██") && !line.Contains("Version")) // Filter progress bars
                            {
                                // _logger.LogInformation($"[DSD] {line}");
                            }
                        }
                    }
                }
                catch {}
            }, token);

        });
    }

    private void HandleActivity()
    {
        // Valid Frame detected
        if (_state.Status != "RECEIVING")
        {
            UpdateState(_state with { Status = "RECEIVING" });
            StartRecording();
        }

        // Reset inactivity timer
        _activityTimeoutCts?.Cancel();
        _activityTimeoutCts = new CancellationTokenSource();
        
        // Reset session timeout (Keep listening)
        if (!_manualOverride)
        {
            RestartSessionTimeout(5000); // 5s hang time
        }

        // If no more activity for 2s, stop recording and revert state
        Task.Delay(2000, _activityTimeoutCts.Token).ContinueWith(t => 
        {
            if (!t.IsCanceled)
            {
                StopRecording();
                if (_manualOverride) UpdateState(_state with { Status = "MONITORING" });
            }
        });
    }

    private void StartRecording()
    {
        if (_recordingStream != null || _state.CurrentChannel == null) return;
        if (DateTime.UtcNow < _recordingLockoutUntil) return;

        var filename = $"rec_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{_state.CurrentChannel.Frequency}.raw";
        // Ensure data dir exists
        var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "../../server/data/recordings");
        if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);

        _currentRecordingPath = Path.Combine(dataDir, filename);
        _recordingStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        try 
        {
            _recordingStream = new FileStream(_currentRecordingPath, FileMode.Create);
            _logger.LogInformation($"Starting recording: {filename}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create recording file");
        }
    }

    private void StopRecording()
    {
        if (_recordingStream == null) return;

        _recordingStream.Close();
        _recordingStream = null;
        
        var duration = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _recordingStartTime) / 1000.0;
        
        if (duration > 2.0 && _currentRecordingPath != null && File.Exists(_currentRecordingPath) && _state.CurrentChannel != null)
        {
             var log = new CallLog(
                 $"log_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                 DateTime.UtcNow.ToString("o"),
                 _state.CurrentChannel.Frequency,
                 _state.CurrentChannel.AlphaTag,
                 _state.CurrentChannel.Description,
                 _state.Gps?.Lat,
                 _state.Gps?.Lon,
                 Path.GetFileName(_currentRecordingPath),
                 duration
             );

             _db.SaveTransmission(log);
             _logger.LogInformation($"Saved transmission: {duration:F1}s");
             OnNewLog?.Invoke(log);
        }
        else if (_currentRecordingPath != null)
        {
            try { File.Delete(_currentRecordingPath); } catch { }
        }
        
        _currentRecordingPath = null;
    }
}