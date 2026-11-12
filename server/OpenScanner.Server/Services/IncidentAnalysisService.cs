using System.Text.RegularExpressions;
using System.Threading.Channels;
using Channel = System.Threading.Channels.Channel;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

/// <summary>
/// AI call analysis. Each finished transcript is sent to the remote WhisperServer's
/// <c>/transmission</c> endpoint, which returns the call type, a summary, the incident
/// status and a geocoded location (or <c>{}</c> for routine chatter). The result is stored
/// in <c>transmission_insights</c> and re-broadcast on the live log so the map updates in
/// real time.
///
/// It runs only when the <c>AiFeaturesEnabled</c> setting is on, a remote server URL is
/// set, and that server advertises <c>/transmission</c>. Work runs on its own queue with
/// one worker, so a slow LLM never holds up transcription. An on-demand backfill
/// analyzes transmissions that were transcribed before the feature was on; it yields to
/// the live queue the same way the transcription backfill does.
/// </summary>
public class IncidentAnalysisService : IHostedService, IDisposable
{
    /// <summary>The setting that turns AI features on.</summary>
    public const string EnabledSetting = "AiFeaturesEnabled";

    private const int BackfillBatchSize = 50;

    private readonly ITranscriptionService _transcription;
    private readonly IDatabase _db;
    private readonly RemoteTranscriptionClient _remote;
    private readonly ILogger<IncidentAnalysisService> _logger;

    private readonly Channel<CallLog> _queue = System.Threading.Channels.Channel.CreateUnbounded<CallLog>();
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;
    private int _disposed;

    // Backfill state, guarded by _lock.
    private readonly object _lock = new();
    private CancellationTokenSource? _backfillCts;
    private bool _running;
    private int _total, _processed, _succeeded, _failed;
    private string? _current, _startedUtc, _finishedUtc, _message;

    /// <summary>
    /// Raised when a transmission has been analyzed as an incident. The log's
    /// <see cref="CallLog.Insight"/> is set.
    /// </summary>
    public event Action<CallLog>? OnInsightReady;

    public IncidentAnalysisService(ITranscriptionService transcription, IDatabase db, RemoteTranscriptionClient remote, ILogger<IncidentAnalysisService> logger)
    {
        _transcription = transcription;
        _db = db;
        _remote = remote;
        _logger = logger;
    }

    /// <summary>Number of live transcripts waiting to be analyzed.</summary>
    public int QueuedCount => _queue.Reader.CanCount ? _queue.Reader.Count : 0;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _transcription.OnTranscriptionCompleted += OnTranscriptionCompleted;
        _worker = Task.Run(() => WorkerLoop(_cts.Token));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _transcription.OnTranscriptionCompleted -= OnTranscriptionCompleted;
        StopBackfill();
        if (Volatile.Read(ref _disposed) == 0) _cts.Cancel();
        return Task.CompletedTask;
    }

    // Raised on a transcription worker thread: just enqueue.
    private void OnTranscriptionCompleted(CallLog log)
    {
        if (string.IsNullOrWhiteSpace(log.Transcription)) return;
        _queue.Writer.TryWrite(log);
    }

    private async Task WorkerLoop(CancellationToken ct)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct))
            {
                while (_queue.Reader.TryRead(out var log))
                {
                    try
                    {
                        await AnalyzeAsync(log, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "AI analysis failed for {Id}", log.Id);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Whether AI analysis can run now: enabled in settings, a remote URL is set and the
    /// server supports <c>/transmission</c>.
    /// </summary>
    public async Task<bool> IsActiveAsync(CancellationToken ct = default)
    {
        var enabled = await _db.GetSettingAsync(EnabledSetting);
        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase)) return false;
        var url = await _remote.GetConfiguredUrlAsync();
        if (url == null) return false;
        return await _remote.HasTransmissionRouteAsync(url, ct);
    }

    /// <summary>
    /// Analyzes one transmission if AI analysis is active and the transcript is worth
    /// sending. Returns the stored insight, or null when it was skipped or the server
    /// failed (in which case nothing is stored, so the backfill can retry it).
    /// </summary>
    internal async Task<TransmissionInsight?> AnalyzeAsync(CallLog log, CancellationToken ct)
    {
        if (!IsWorthAnalyzing(log.Transcription)) return null;
        if (!await IsActiveAsync(ct)) return null;

        var insight = await _remote.ExtractTransmissionAsync(log.Transcription!, ServiceHint(log.AlphaTag, log.Description), ct);
        if (insight == null) return null;

        insight.TransmissionId = log.Id;
        await _db.SaveInsightAsync(insight);

        if (insight.IsIncident)
        {
            log.Insight = insight;
            _logger.LogInformation("AI analysis for {Id}: {CallType} at {Address}", log.Id, insight.CallType,
                insight.MatchedAddress ?? insight.Address ?? insight.PlaceName ?? "(no location)");
            OnInsightReady?.Invoke(log);
        }
        return insight;
    }

    /// <summary>A transcript needs at least two words to be worth an LLM call.</summary>
    internal static bool IsWorthAnalyzing(string? transcript) =>
        !string.IsNullOrWhiteSpace(transcript)
        && transcript.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 2;

    private static readonly Regex FireWords = new(@"\b(fire|fd|ems|rescue|engine|ladder|medic|ambulance)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PoliceWords = new(@"\b(pd|police|law|sheriff|trooper|patrol)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Guesses the service from the channel name/description, which the server uses
    /// instead of inferring it. Null when it is unclear, so the server decides.
    /// </summary>
    internal static string? ServiceHint(string? alphaTag, string? description)
    {
        var text = $"{alphaTag} {description}";
        var fire = FireWords.IsMatch(text);
        var police = PoliceWords.IsMatch(text);
        if (fire == police) return null;
        return fire ? "fire" : "police";
    }

    // ---- Backfill ----

    /// <summary>Status of the history analysis job.</summary>
    public BackfillStatus GetBackfillStatus()
    {
        lock (_lock)
        {
            return new BackfillStatus(_running, _total, _processed, _succeeded, _failed, _current, _startedUtc, _finishedUtc, _message);
        }
    }

    /// <summary>Starts analyzing past transcripts. No-op when already running.</summary>
    public bool StartBackfill()
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
            _backfillCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var token = _backfillCts.Token;
            _ = Task.Run(() => RunBackfillAsync(token));
        }
        _logger.LogInformation("AI analysis backfill started.");
        return true;
    }

    /// <summary>Asks the history analysis job to stop after the current transmission.</summary>
    public void StopBackfill()
    {
        lock (_lock)
        {
            if (!_running) return;
            _backfillCts?.Cancel();
            _message = "Stopping…";
        }
    }

    private async Task RunBackfillAsync(CancellationToken ct)
    {
        try
        {
            if (!await IsActiveAsync(ct))
            {
                lock (_lock) _message = "AI analysis is off or the remote server does not support it.";
                return;
            }

            var total = await _db.CountUnanalyzedAsync();
            lock (_lock)
            {
                _total = total;
                _message = total == 0 ? "Every transcribed call has been analyzed." : $"Found {total} call(s) to analyze.";
            }

            // Rows that fail (server error) stay unanalyzed and would come back in the next
            // batch; remember them so one run never loops on the same rows.
            var attempted = new HashSet<string>();
            while (!ct.IsCancellationRequested)
            {
                var batch = (await _db.GetUnanalyzedAsync(BackfillBatchSize + attempted.Count))
                    .Where(l => !attempted.Contains(l.Id))
                    .Take(BackfillBatchSize)
                    .ToList();
                if (batch.Count == 0) break;

                foreach (var log in batch)
                {
                    if (ct.IsCancellationRequested) break;

                    // Low priority: live transcripts go first.
                    while (!ct.IsCancellationRequested && QueuedCount > 0)
                    {
                        lock (_lock) _message = "Paused: yielding to live calls…";
                        await Task.Delay(2000, ct);
                    }
                    if (ct.IsCancellationRequested) break;

                    attempted.Add(log.Id);
                    lock (_lock) { _current = log.Id; _message = "Analyzing…"; }

                    TransmissionInsight? result;
                    if (IsWorthAnalyzing(log.Transcription))
                    {
                        result = await AnalyzeAsync(log, ct);
                    }
                    else
                    {
                        // Too short to send: record it as routine so it is not offered again.
                        result = new TransmissionInsight { TransmissionId = log.Id, AnalyzedAt = DateTime.UtcNow.ToString("o") };
                        await _db.SaveInsightAsync(result);
                    }

                    lock (_lock)
                    {
                        _processed++;
                        if (result != null) _succeeded++; else _failed++;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped; handled in finally.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI analysis backfill failed");
            lock (_lock) _message = "Error: " + ex.Message;
        }
        finally
        {
            lock (_lock)
            {
                var cancelled = _backfillCts?.IsCancellationRequested == true;
                _running = false;
                _current = null;
                _finishedUtc = DateTime.UtcNow.ToString("o");
                if (cancelled) _message = $"Stopped. {_processed}/{_total} analyzed.";
                else if (_total > 0) _message = $"Done. {_succeeded} analyzed, {_failed} failed of {_total}.";
                _backfillCts?.Dispose();
                _backfillCts = null;
            }
            _logger.LogInformation("AI analysis backfill finished.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The service is registered as a singleton and as a hosted service that resolves to
        // that same instance, so the container disposes it twice. Only the first call counts.
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
