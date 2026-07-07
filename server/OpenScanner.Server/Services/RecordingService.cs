using System.Collections.Concurrent;
using System.Diagnostics;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

public class RecordingService : IRecordingService
{
    private readonly IDatabase _db;
    private readonly ILogger<RecordingService> _logger;
    private readonly ITranscriptionService _transcriptionService;
    private readonly GpsService _gpsService;

    // Legacy single-channel recording state (used by non-parallel mode)
    private readonly object _audioLock = new();
    private FileStream? _recordingStream;
    private string? _currentRecordingPath;
    private long _recordingStartTime;
    private int? _currentSourceID;
    private int? _currentTargetID;
    private readonly List<int> _speakerList = new();

    // Multi-channel recording state (used by parallel mode)
    private readonly ConcurrentDictionary<double, ActiveRecording> _activeRecordings = new();

    public event Action<CallLog>? OnNewLog;

    /// <inheritdoc />
    public bool IsRecording
    {
        get
        {
            lock (_audioLock) return _recordingStream != null;
        }
    }

    /// <inheritdoc />
    public bool IsChannelRecording(double frequency) => _activeRecordings.ContainsKey(frequency);

    /// <inheritdoc />
    public string? CurrentRecordingId
    {
        get
        {
            lock (_audioLock)
            {
                if (_recordingStream != null) return $"log_{_recordingStartTime}";
            }

            if (!_activeRecordings.IsEmpty)
            {
                var latest = _activeRecordings.Values
                    .OrderByDescending(r => r.StartTime)
                    .FirstOrDefault();
                if (latest != null) return $"log_{latest.StartTime}";
            }

            return null;
        }
    }

    public RecordingService(
        IDatabase db, 
        ILogger<RecordingService> logger, 
        ITranscriptionService transcriptionService,
        GpsService gpsService)
    {
        _db = db;
        _logger = logger;
        _transcriptionService = transcriptionService;
        _gpsService = gpsService;

        _transcriptionService.OnTranscriptionCompleted += (log) =>
        {
            _logger.LogInformation($"Transcription completed for log {log.Id}: {log.Transcription}");
            OnNewLog?.Invoke(log);
        };
    }

    public void UpdateSourceTarget(int? src, int? tgt)
    {
        if (src.HasValue) _currentSourceID = src;
        if (tgt.HasValue) _currentTargetID = tgt;
    }

    public void AppendSpeaker(int src)
    {
        if (_speakerList.Count == 0 || _speakerList[^1] != src)
            _speakerList.Add(src);
    }

    public void StartRecording(Channel channel, int? src, int? tgt, LinkedList<byte[]> preRollBuffer)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var filename = $"rec_{now}_{channel.Frequency}.raw";
        
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
            _speakerList.Clear();
            if (src.HasValue) _speakerList.Add(src.Value);

            try 
            {
                _recordingStream = new FileStream(_currentRecordingPath, FileMode.Create);
                _logger.LogInformation($"Starting recording: {filename}");

                // Flush pre-roll buffer
                if (preRollBuffer != null)
                {
                    lock(preRollBuffer) // Safety lock on the collection
                    {
                        foreach (var chunk in preRollBuffer)
                        {
                            _recordingStream.Write(chunk, 0, chunk.Length);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create recording file");
            }
        }
    }

    public void ProcessAudio(byte[] audio)
    {
        lock (_audioLock)
        {
            if (_recordingStream != null && _recordingStream.CanWrite)
            {
                try
                {
                    _recordingStream.Write(audio, 0, audio.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error writing to recording stream");
                }
            }
        }
    }

    public void StopRecording(Channel channel, string? lastDetectedTone)
    {
        string? recordingPath;
        long startTime;
        string? speakerChain;
        int? sourceID;
        int? targetID;

        lock (_audioLock)
        {
            if (_recordingStream == null) return;
            _recordingStream.Close();
            _recordingStream = null;
            recordingPath = _currentRecordingPath;
            startTime = _recordingStartTime;
            sourceID = _currentSourceID;
            targetID = _currentTargetID;
            speakerChain = _speakerList.Count > 1
                ? string.Join(" → ", _speakerList)
                : null;
            _currentRecordingPath = null;
        }

        // Shared convert (RAW -> MP3), save, and transcribe logic.
        FinalizeRecording(recordingPath, startTime, channel, sourceID, targetID,
                          lastDetectedTone, speakerChain);
    }

    // --- Multi-channel (parallel mode) methods ---

    /// <inheritdoc />
    public void UpdateSourceTarget(double frequency, int? src, int? tgt)
    {
        if (_activeRecordings.TryGetValue(frequency, out var rec))
        {
            lock (rec.Lock)
            {
                if (src.HasValue) rec.SourceID = src;
                if (tgt.HasValue) rec.TargetID = tgt;
            }
        }
    }

    /// <inheritdoc />
    public void AppendSpeaker(double frequency, int src)
    {
        if (_activeRecordings.TryGetValue(frequency, out var rec))
        {
            lock (rec.Lock)
            {
                if (rec.SpeakerList.Count == 0 || rec.SpeakerList[^1] != src)
                    rec.SpeakerList.Add(src);
            }
        }
    }

    /// <inheritdoc />
    public void ProcessAudio(double frequency, byte[] audio)
    {
        if (_activeRecordings.TryGetValue(frequency, out var rec))
        {
            lock (rec.Lock)
            {
                if (rec.Stream != null && rec.Stream.CanWrite)
                {
                    try
                    {
                        rec.Stream.Write(audio, 0, audio.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error writing to recording stream for {frequency} MHz");
                    }
                }
            }
        }
        else
        {
            // Fallback: route to legacy single-channel recording
            ProcessAudio(audio);
        }
    }

    /// <summary>
    /// Start a multi-channel recording for the given channel (parallel mode).
    /// Uses the ConcurrentDictionary-based storage keyed by frequency.
    /// </summary>
    public void StartParallelRecording(Channel channel, int? src, int? tgt, LinkedList<byte[]>? preRollBuffer)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var filename = $"rec_{now}_{channel.Frequency}.raw";
        var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "../../data/recordings");
        if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, filename);

        var rec = new ActiveRecording
        {
            Path = path,
            StartTime = now,
            SourceID = src,
            TargetID = tgt,
            Channel = channel
        };
        if (src.HasValue) rec.SpeakerList.Add(src.Value);

        if (!_activeRecordings.TryAdd(channel.Frequency, rec))
        {
            _logger.LogWarning($"Recording already active for {channel.Frequency} MHz, skipping");
            return;
        }

        try
        {
            rec.Stream = new FileStream(path, FileMode.Create);
            _logger.LogInformation($"Started parallel recording: {filename}");

            if (preRollBuffer != null)
            {
                lock (preRollBuffer)
                {
                    foreach (var chunk in preRollBuffer)
                        rec.Stream.Write(chunk, 0, chunk.Length);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to create recording file for {channel.Frequency} MHz");
            _activeRecordings.TryRemove(channel.Frequency, out _);
        }
    }

    /// <summary>
    /// Stop a multi-channel recording and finalize (convert, transcribe, save to DB).
    /// </summary>
    public void StopParallelRecording(double frequency, string? lastDetectedTone)
    {
        if (!_activeRecordings.TryRemove(frequency, out var rec))
            return;

        string? recordingPath;
        long startTime;
        string? speakerChain;
        Channel channel;

        lock (rec.Lock)
        {
            rec.Stream?.Close();
            rec.Stream = null;
            recordingPath = rec.Path;
            startTime = rec.StartTime;
            channel = rec.Channel;
            speakerChain = rec.SpeakerList.Count > 1
                ? string.Join(" \u2192 ", rec.SpeakerList)
                : null;
        }

        FinalizeRecording(recordingPath, startTime, channel, rec.SourceID, rec.TargetID,
                          lastDetectedTone, speakerChain);
    }

    /// <summary>
    /// Shared finalization logic: convert RAW to WAV, transcribe, save to DB.
    /// </summary>
    private void FinalizeRecording(string? recordingPath, long startTime, Channel channel,
                                    int? sourceID, int? targetID,
                                    string? lastDetectedTone, string? speakerChain)
    {
        var duration = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startTime) / 1000.0;
        _logger.LogInformation($"Recording duration: {duration:F1}s. Raw file exists: {File.Exists(recordingPath)}");

        if (duration >= 0.5 && recordingPath != null && File.Exists(recordingPath) && channel != null)
        {
            var fileInfo = new FileInfo(recordingPath);
            if (fileInfo.Length < 4096)
            {
                try { File.Delete(recordingPath); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete small recording file"); }
                _logger.LogInformation("Deleted small recording file (less than 4KB).");
                return;
            }

            // Convert RAW to compressed MP3 (mono, 32 kbps) to save disk space.
            var mp3Path = Path.ChangeExtension(recordingPath, ".mp3");
            try
            {
                var convertStart = new ProcessStartInfo(PlatformTools.Ffmpeg)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                // Input: raw 48k mono s16le
                convertStart.ArgumentList.Add("-f"); convertStart.ArgumentList.Add("s16le");
                convertStart.ArgumentList.Add("-ar"); convertStart.ArgumentList.Add("48000");
                convertStart.ArgumentList.Add("-ac"); convertStart.ArgumentList.Add("1");
                convertStart.ArgumentList.Add("-i"); convertStart.ArgumentList.Add(recordingPath);
                // Output: MP3, mono, 32 kbps
                convertStart.ArgumentList.Add("-codec:a"); convertStart.ArgumentList.Add("libmp3lame");
                convertStart.ArgumentList.Add("-b:a"); convertStart.ArgumentList.Add("32k");
                convertStart.ArgumentList.Add("-ac"); convertStart.ArgumentList.Add("1");
                convertStart.ArgumentList.Add(mp3Path);
                convertStart.ArgumentList.Add("-y");

                using (var proc = Process.Start(convertStart))
                {
                    string output = proc?.StandardOutput.ReadToEnd() ?? string.Empty;
                    string error = proc?.StandardError.ReadToEnd() ?? string.Empty;
                    proc?.WaitForExit();

                    if (!string.IsNullOrEmpty(output)) _logger.LogInformation($"FFmpeg Output: {output}");
                    if (!string.IsNullOrEmpty(error)) _logger.LogError($"FFmpeg Error: {error}");
                }

                if (File.Exists(mp3Path))
                {
                    _logger.LogInformation($"MP3 file created successfully: {mp3Path}");
                    try { File.Delete(recordingPath); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete original RAW file after conversion"); }
                    recordingPath = mp3Path;
                }
                else
                {
                    _logger.LogError($"MP3 file was not created by FFmpeg at {mp3Path}.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MP3 conversion failed");
            }

            var gps = _gpsService.GetLastLocation();
            var lat = gps?.Lat ?? 0;
            var lon = gps?.Lon ?? 0;

            var log = new CallLog(
                 $"log_{startTime}",
                 DateTimeOffset.FromUnixTimeMilliseconds(startTime).UtcDateTime.ToString("o"),
                 channel.Frequency,
                 channel.AlphaTag,
                 channel.Description,
                 lat != 0 ? lat : null,
                 lon != 0 ? lon : null,
                 Path.GetFileName(recordingPath),
                 duration,
                 null, // transcription starts as null and is populated asynchronously
                 sourceID,
                 targetID,
                 lastDetectedTone,
                 speakerChain
             );

            _logger.LogInformation($"Recording path before saving: {recordingPath}, exists: {File.Exists(recordingPath)}");
            _db.SaveTransmissionAsync(log).ContinueWith(t => {
                if (t.IsFaulted) _logger.LogError(t.Exception, "Failed to save transmission");
            });

            _logger.LogInformation($"Saved transmission: {duration:F1}s | RID: {sourceID} | Tone: {lastDetectedTone} | Text: (queued)");
            _logger.LogInformation($"Recording saved to: {recordingPath}");
            OnNewLog?.Invoke(log);

            // Queue transcription in the background
            _transcriptionService.QueueTranscription(log, recordingPath);
        }
        else if (recordingPath != null)
        {
            try { File.Delete(recordingPath); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete aborted recording file"); }
        }
    }
}

/// <summary>
/// State for a single active recording in parallel mode.
/// </summary>
internal class ActiveRecording
{
    public readonly object Lock = new();
    public FileStream? Stream;
    public string Path = string.Empty;
    public long StartTime;
    public int? SourceID;
    public int? TargetID;
    public Channel Channel = new();
    public readonly List<int> SpeakerList = new();
}