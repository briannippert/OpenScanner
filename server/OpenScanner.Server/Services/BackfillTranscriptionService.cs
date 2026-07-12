using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

/// <summary>
/// Low-priority, on-demand job that scans the last 24 hours of recordings for
/// missing transcriptions and re-transcribes them. It pauses whenever the live
/// transcription queue has work so real-time transmissions keep priority.
/// </summary>
public class BackfillTranscriptionService : IBackfillService
{
    private readonly IDatabase _db;
    private readonly ITranscriptionService _transcription;
    private readonly ILogger<BackfillTranscriptionService> _logger;

    // How far back to look for missing transcriptions.
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    // Status fields, guarded by _lock.
    private bool _running;
    private int _total, _processed, _succeeded, _failed;
    private string? _current, _startedUtc, _finishedUtc, _message;

    public BackfillTranscriptionService(IDatabase db, ITranscriptionService transcription, ILogger<BackfillTranscriptionService> logger)
    {
        _db = db;
        _transcription = transcription;
        _logger = logger;
    }

    public BackfillStatus GetStatus()
    {
        lock (_lock)
        {
            return new BackfillStatus(_running, _total, _processed, _succeeded, _failed, _current, _startedUtc, _finishedUtc, _message);
        }
    }

    public bool Start()
    {
        lock (_lock)
        {
            if (_running) return false;
            _running = true;
            _total = _processed = _succeeded = _failed = 0;
            _current = null;
            _finishedUtc = null;
            _startedUtc = DateTime.UtcNow.ToString("o");
            _message = "Starting…";
            _cts = new CancellationTokenSource();
            _runTask = Task.Run(() => RunAsync(_cts.Token));
        }
        _logger.LogInformation("Transcription backfill started.");
        return true;
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_running) return;
            _cts?.Cancel();
            _message = "Stopping…";
        }
        _logger.LogInformation("Transcription backfill stop requested.");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var since = DateTime.UtcNow - Window;
            var pending = (await _db.GetUntranscribedSinceAsync(since)).ToList();

            lock (_lock)
            {
                _total = pending.Count;
                _message = pending.Count == 0
                    ? "Nothing to backfill in the last 24 hours."
                    : $"Found {pending.Count} clip(s) to re-transcribe.";
            }

            var recordingsDir = Path.GetFullPath(
                Path.Combine(Directory.GetCurrentDirectory(), "../../data/recordings"));

            foreach (var log in pending)
            {
                if (ct.IsCancellationRequested) break;

                // Low priority: while live transcription has queued work, wait.
                while (!ct.IsCancellationRequested && _transcription.GetQueueStatus().Queued > 0)
                {
                    lock (_lock) _message = "Paused: yielding to live transcription queue…";
                    await Task.Delay(2000, ct);
                }
                if (ct.IsCancellationRequested) break;

                var name = log.AudioPath;
                lock (_lock) { _current = name; _message = "Re-transcribing…"; }

                try
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        lock (_lock) { _processed++; _failed++; }
                        continue;
                    }

                    var audioPath = Path.Combine(recordingsDir, name);
                    if (!File.Exists(audioPath))
                    {
                        _logger.LogDebug("Backfill: audio file missing for {Id} at {Path}", log.Id, audioPath);
                        lock (_lock) { _processed++; _failed++; }
                        continue;
                    }

                    var ok = await _transcription.RetranscribeAsync(log, audioPath);
                    lock (_lock)
                    {
                        _processed++;
                        if (ok) _succeeded++; else _failed++;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Backfill failed for {Id}", log.Id);
                    lock (_lock) { _processed++; _failed++; }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped — handled in finally.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transcription backfill run failed");
            lock (_lock) _message = "Error: " + ex.Message;
        }
        finally
        {
            lock (_lock)
            {
                var cancelled = _cts?.IsCancellationRequested == true;
                _running = false;
                _current = null;
                _finishedUtc = DateTime.UtcNow.ToString("o");
                if (cancelled)
                {
                    _message = $"Stopped. {_processed}/{_total} processed ({_succeeded} transcribed).";
                }
                else if (_total > 0)
                {
                    _message = $"Done. {_succeeded} transcribed, {_failed} failed of {_total}.";
                }
                _cts?.Dispose();
                _cts = null;
            }
            _logger.LogInformation("Transcription backfill finished.");
        }
    }
}
