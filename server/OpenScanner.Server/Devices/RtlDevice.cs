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
    private const int PreRollMaxBytes = 96000 * 5; // ~5 seconds at 48kHz 16-bit to capture full transmission start

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

        var banks = CalculateScanBanks(_channelService.Channels);
        var rate = 2048000;

        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        Task.Run(() => RunScannerLoop(banks, rate, token), token);
    }

    /// <summary>
    /// Main scanner loop that cycles through scan banks.
    /// For FastScan mode banks, scans continuously without dwell timer.
    /// For FrequencyHop mode banks, uses dwell-based hopping.
    /// </summary>
    private async Task RunScannerLoop(List<ScanBank> banks, int sampleRate, CancellationToken token)
    {
        int bankIndex = 0;
        
        while (!token.IsCancellationRequested)
        {
            if (banks.Count == 0) break;

            var bank = banks[bankIndex];
            
            if (bank.Mode == ScanMode.FastScan)
            {
                // FastScan: Continuous scanning without dwell timer
                _logger.LogDebug($"FastScan mode: Center={bank.CenterFrequency:F3} MHz, Spread={bank.SpreadMHz:F2} MHz");
                
                try
                {
                    await RunSingleScanSegment(bank.CenterFrequency, sampleRate, token);
                }
                catch (OperationCanceledException)
                {
                    // Expected when signal lock or manual stop
                }
            }
            else
            {
                // FrequencyHop: Dwell-based hopping with 2-second timer
                _logger.LogDebug($"FrequencyHop mode: Center={bank.CenterFrequency:F3} MHz, Dwell={bank.DwellTimeMs}ms");
                
                // Only use dwell-hopping if we have multiple banks to cycle through
                if (banks.Count > 1)
                {
                    using var dwellCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    dwellCts.CancelAfter(bank.DwellTimeMs);

                    try 
                    {
                        await RunSingleScanSegment(bank.CenterFrequency, sampleRate, dwellCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when dwell time expires or main token cancelled
                    }
                }
                else
                {
                    // Single bank: Run until token is cancelled
                    await RunSingleScanSegment(bank.CenterFrequency, sampleRate, token);
                }
            }
            
            if (token.IsCancellationRequested) break;
            
            bankIndex = (bankIndex + 1) % banks.Count;
        }

        // Loop exited
        if (!token.IsCancellationRequested && _state.Status == "SCANNING")
        {
            UpdateState(_state with { Status = "IDLE" });
        }
    }

    private async Task RunSingleScanSegment(double centerFreqMhz, int sampleRate, CancellationToken token)
    {
        // 2.5 second delay to ensure hardware is fully reset and ready
        await Task.Delay(2500, token);

        // Standard stable sample rate for R820T tuner
        int scanRate = 1024000; 
        var binPath = "/usr/bin/rtl_sdr";
        // -b 1: Use 1 buffer to reduce latency and improve startup reliability
        var args = $"-f {centerFreqMhz:F3}M -s {scanRate} -g 20 -b 1 -";

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
                    
                    _logger.LogInformation($"Scanner HW: {line}");

                    if (line.Contains("Found") || line.Contains("Using device")) 
                        UpdateState(_state with { IsHardwareConnected = true });
                    if (line.Contains("No supported devices") || line.Contains("Failed to open"))
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
        var segmentStartTime = DateTime.UtcNow;

        try
        {
            long totalBytesRead = 0;
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


                // Rate limit FFT updates (approx 50Hz)
                if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds < 20) continue;
                lastUpdate = DateTime.UtcNow;

                // Warm-up: Skip first 200ms of data
                if ((DateTime.UtcNow - segmentStartTime).TotalMilliseconds < 200) continue;

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
    }

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
    /// Calculate optimized scan banks with mixed fast/hop strategy.
    /// If all channels fit in 2.4 MHz window, uses FastScan mode (continuous).
    /// Otherwise, breaks into 0.5 MHz clusters with FrequencyHop mode (2-second dwell).
    /// </summary>
    private List<ScanBank> CalculateScanBanks(List<Channel> channels)
    {
        if (!channels.Any()) return new List<ScanBank>();
        
        var sorted = channels.OrderBy(c => c.Frequency).ToList();
        var banks = new List<ScanBank>();
        
        // First pass: group contiguous frequencies (allowing larger gaps than 0.5 MHz initially)
        var groups = new List<List<Channel>>();
        var currentGroup = new List<Channel> { sorted[0] };
        
        for (int i = 1; i < sorted.Count; i++)
        {
            var c = sorted[i];
            // Check if there's a significant gap (> 2 MHz)
            // 3.0 MHz gap threshold: keeps 2.5 MHz spans grouped (→ FrequencyHop)
            // while still splitting truly disjoint bands (e.g. 3.5 MHz apart → separate FastScan)
            if (c.Frequency - currentGroup.Last().Frequency > 3.0)
            {
                groups.Add(currentGroup);
                currentGroup = new List<Channel> { c };
            }
            else
            {
                currentGroup.Add(c);
            }
        }
        if (currentGroup.Any()) groups.Add(currentGroup);
        
        // Second pass: for each contiguous group, decide FastScan vs FrequencyHop
        foreach (var group in groups)
        {
            var minFreq = group.Min(x => x.Frequency);
            var maxFreq = group.Max(x => x.Frequency);
            var spread = maxFreq - minFreq;
            
            if (spread <= 2.4 + 1e-9)  // Small epsilon for floating point safety
            {
                // FastScan mode: all frequencies fit in 2.4 MHz window
                var center = (minFreq + maxFreq) / 2.0;
                var frequencies = group.Select(x => x.Frequency).ToList();
                
                _logger.LogInformation($"ScanBank FastScan: {minFreq:F3}-{maxFreq:F3} MHz (spread: {spread:F2} MHz)");
                
                banks.Add(new ScanBank
                {
                    CenterFrequency = center,
                    Frequencies = frequencies,
                    SpreadMHz = spread,
                    Mode = ScanMode.FastScan,
                    DwellTimeMs = 0  // Continuous scanning
                });
            }
            else
            {
                // FrequencyHop mode: break into 0.5 MHz sub-clusters
                var subClusters = new List<List<Channel>>();
                var currentCluster = new List<Channel> { group[0] };
                
                for (int i = 1; i < group.Count; i++)
                {
                    var c = group[i];
                    if (c.Frequency - currentCluster[0].Frequency > 0.5)
                    {
                        subClusters.Add(currentCluster);
                        currentCluster = new List<Channel> { c };
                    }
                    else
                    {
                        currentCluster.Add(c);
                    }
                }
                if (currentCluster.Any()) subClusters.Add(currentCluster);
                
                // Create a ScanBank for each sub-cluster
                foreach (var subCluster in subClusters)
                {
                    var subMinFreq = subCluster.Min(x => x.Frequency);
                    var subMaxFreq = subCluster.Max(x => x.Frequency);
                    var subSpread = subMaxFreq - subMinFreq;
                    var subCenter = (subMinFreq + subMaxFreq) / 2.0 + 0.25;  // Offset by 250kHz
                    var frequencies = subCluster.Select(x => x.Frequency).ToList();
                    
                    _logger.LogInformation($"ScanBank FrequencyHop: {subMinFreq:F3}-{subMaxFreq:F3} MHz (spread: {subSpread:F2} MHz)");
                    
                    banks.Add(new ScanBank
                    {
                        CenterFrequency = subCenter,
                        Frequencies = frequencies,
                        SpreadMHz = subSpread,
                        Mode = ScanMode.FrequencyHop,
                        DwellTimeMs = 2000
                    });
                }
            }
        }
        
        return banks;
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
                // DC Filter: Ignore center 3 bins (approx 24kHz spread)
                int centerBin = fftSize / 2;
                if (binIndex >= centerBin - 1 && binIndex <= centerBin + 1)
                {
                    // If we have a strong signal in the center bin, warn that it might be getting filtered
                    if (fftDb[binIndex] > -40) 
                    {
                        _logger.LogDebug($"Strong signal detected in center bin ({channel.Frequency} MHz). DC filter is rejecting it.");
                    }
                    continue;
                }

                double db = fftDb[binIndex];
                if (db > maxDetectedDb) maxDetectedDb = db;

                // SNR Requirement: Signal must be at least 15dB above average noise
                // AND above absolute threshold
                double snr = db - avgNoise;
                double threshold = _state.Squelch ?? -55;

                if (db > threshold)
                {
                    if (snr > 15)
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
                        _logger.LogDebug($"Signal detected on {channel.AlphaTag} but SNR too low: {snr:F1}dB (Need 15dB)");
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

        // Delay starting the decoder to let hardware settle after scanner stop
        Task.Delay(2000).ContinueWith(_ => {
            if (_decodeCts != null && _decodeCts.IsCancellationRequested) return;
            StartDecoding(channel);
        });

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
        
        // Don't clear pre-roll buffer here - we want to maintain history
        // across decoder switches and transmission starts. It will self-regulate by max size.
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
