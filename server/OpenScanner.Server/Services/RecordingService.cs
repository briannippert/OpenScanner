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

    private readonly object _audioLock = new();
    private FileStream? _recordingStream;
    private string? _currentRecordingPath;
    private long _recordingStartTime;
    private int? _currentSourceID;
    private int? _currentTargetID;

    public event Action<CallLog>? OnNewLog;

    public bool IsRecording 
    {
        get 
        {
            lock (_audioLock) return _recordingStream != null;
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
        
        lock (_audioLock)
        {
            if (_recordingStream == null) return;
            _recordingStream.Close();
            _recordingStream = null;
            recordingPath = _currentRecordingPath;
            startTime = _recordingStartTime;
        }

        var duration = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startTime) / 1000.0;
        _logger.LogInformation($"Recording duration: {duration:F1}s. Raw file exists: {File.Exists(recordingPath)}");

        if (duration >= 0.5 && recordingPath != null && File.Exists(recordingPath))
        {
             var fileInfo = new FileInfo(recordingPath);
             if (fileInfo.Length < 4096) 
             {
                 try { File.Delete(recordingPath); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete small recording file"); }
                 _logger.LogInformation("Deleted small recording file (less than 4KB).");
                 return;
             }

             if (channel != null)
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
                         string output = proc?.StandardOutput.ReadToEnd() ?? string.Empty;
                         string error = proc?.StandardError.ReadToEnd() ?? string.Empty;
                         proc?.WaitForExit();

                         if (!string.IsNullOrEmpty(output)) _logger.LogInformation($"FFmpeg Output: {output}");
                         if (!string.IsNullOrEmpty(error)) _logger.LogError($"FFmpeg Error: {error}");
                     }

                     if (File.Exists(wavPath))
                     {
                         _logger.LogInformation($"WAV file created successfully: {wavPath}");
                         try { File.Delete(recordingPath); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete original RAW file after conversion"); }
                         recordingPath = wavPath;
                     }
                     else
                     {
                         _logger.LogError($"WAV file was not created by FFmpeg at {wavPath}.");
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
                     transcription = _transcriptionService.TranscribeAudio(recordingPath);
                 }
                 catch (Exception ex)
                 {
                     _logger.LogError(ex, "Transcription failed");
                 }

                 var gps = _gpsService.GetLastLocation();
                 var lat = gps?.Lat ?? 0;
                 var lon = gps?.Lon ?? 0;

                 var log = new CallLog(
                      $"log_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                      DateTime.UtcNow.ToString("o"),
                      channel.Frequency,
                      channel.AlphaTag,
                      channel.Description,
                      lat != 0 ? lat : null,
                      lon != 0 ? lon : null,
                      Path.GetFileName(recordingPath),
                      duration,
                      transcription,
                      _currentSourceID,
                      _currentTargetID,
                      lastDetectedTone
                  );
                 
                  
                  _logger.LogInformation($"Recording path before saving: {recordingPath}, exists: {File.Exists(recordingPath)}");
                  _db.SaveTransmissionAsync(log).ContinueWith(t => {
                      if (t.IsFaulted) _logger.LogError(t.Exception, "Failed to save transmission");
                  });
                 
                  _logger.LogInformation($"Saved transmission: {duration:F1}s | RID: {_currentSourceID} | Tone: {lastDetectedTone} | Text: {transcription}");
                 _logger.LogInformation($"Recording saved to: {recordingPath}"); // Add this line
                  OnNewLog?.Invoke(log);
             }
        }
        else if (recordingPath != null)
        {
             try { File.Delete(recordingPath); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete aborted recording file"); }
        }
        
        lock (_audioLock)
        {
            if (_recordingStream == null && _currentRecordingPath == recordingPath)
            {
                _currentRecordingPath = null;
            }
        }
    }
}