using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

/// <summary>
/// Sends a push notification through ntfy (https://ntfy.sh or a self-hosted server)
/// for each fire tone-out. The push waits for the recording carrying the page to be
/// transcribed and uses the dispatch transcript as its body; if no transcript arrives
/// (transcription off, blank result, or <see cref="TranscriptTimeout"/> elapses) it falls
/// back to the channel/frequency/tone details. Configured via the <c>Ntfy*</c> settings:
/// <c>NtfyEnabled</c>, <c>NtfyServerUrl</c> (blank = ntfy.sh), <c>NtfyTopic</c>,
/// <c>NtfyToken</c> (optional bearer token) and <c>NtfyPriority</c> (1-5, default 5).
/// </summary>
public class NtfyNotificationService : IHostedService
{
    public const string DefaultServerUrl = "https://ntfy.sh";
    private const int DefaultPriority = 5;

    /// <summary>A repeat page for the same tone set inside this window is not re-sent.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for the page's transcript before sending without it.</summary>
    public static readonly TimeSpan TranscriptTimeout = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly IRadioSource _radio;
    private readonly ITranscriptionService _transcription;
    private readonly IDatabase _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<NtfyNotificationService> _logger;

    private readonly Dictionary<string, DateTimeOffset> _lastSent = new();
    private readonly List<PendingPage> _pending = new();
    private readonly object _lock = new();

    /// <summary>A detected tone-out waiting for its recording's transcript.</summary>
    private class PendingPage
    {
        public required RadioEvent Event { get; init; }
        public CancellationTokenSource TimeoutCts { get; } = new();
    }

    public NtfyNotificationService(IRadioSource radio, ITranscriptionService transcription, IDatabase db, IHttpClientFactory httpClientFactory,
        TimeProvider time, ILogger<NtfyNotificationService> logger)
    {
        _radio = radio;
        _transcription = transcription;
        _db = db;
        _httpClientFactory = httpClientFactory;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _radio.OnNewEvent += OnRadioEvent;
        _transcription.OnTranscriptionCompleted += OnTranscriptionCompleted;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _radio.OnNewEvent -= OnRadioEvent;
        _transcription.OnTranscriptionCompleted -= OnTranscriptionCompleted;
        lock (_lock)
        {
            foreach (var page in _pending) page.TimeoutCts.Cancel();
            _pending.Clear();
        }
        return Task.CompletedTask;
    }

    // Raised on the detection/audio thread: never block it on the network.
    private void OnRadioEvent(RadioEvent e) => _ = Task.Run(() => HandleEventAsync(e));

    private void OnTranscriptionCompleted(CallLog log) => _ = Task.Run(() => HandleTranscriptionAsync(log));

    /// <summary>
    /// Queues a notification for <paramref name="e"/> if it is a tone-out, notifications are
    /// enabled, and the tone set isn't inside its cooldown. The push goes out when the
    /// matching transcript arrives, or after <see cref="TranscriptTimeout"/>. Never throws.
    /// </summary>
    public async Task HandleEventAsync(RadioEvent e)
    {
        try
        {
            if (e.Type != "TONE_OUT") return;
            if (await LoadConfigAsync() == null) return;

            var now = _time.GetUtcNow();
            var page = new PendingPage { Event = e };
            lock (_lock)
            {
                if (_lastSent.TryGetValue(e.Label, out var last) && now - last < Cooldown) return;
                _lastSent[e.Label] = now;
                _pending.Add(page);
            }

            _ = WaitForTranscriptAsync(page);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue ntfy notification");
        }
    }

    /// <summary>
    /// Sends the pending pages that belong to <paramref name="log"/>, with its transcript as
    /// the body. A page matches when it was raised during this recording, or when the
    /// recording was tagged with the page's tone set. Never throws.
    /// </summary>
    public async Task HandleTranscriptionAsync(CallLog log)
    {
        try
        {
            List<PendingPage> matched;
            lock (_lock)
            {
                matched = _pending.Where(p =>
                    (p.Event.TransmissionId != null && p.Event.TransmissionId == log.Id) ||
                    (log.DetectedTone != null && log.DetectedTone == p.Event.Label)).ToList();
                foreach (var page in matched) _pending.Remove(page);
            }

            foreach (var page in matched)
            {
                page.TimeoutCts.Cancel();
                await SendPageAsync(page.Event, log.Transcription);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send ntfy notification");
        }
    }

    // Safety net: if the transcript never shows up, send the page with its details instead.
    private async Task WaitForTranscriptAsync(PendingPage page)
    {
        try
        {
            await Task.Delay(TranscriptTimeout, _time, page.TimeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        bool stillPending;
        lock (_lock) stillPending = _pending.Remove(page);
        if (!stillPending) return;

        _logger.LogInformation("No transcript for tone-out {Label} after {Timeout}; sending without it", page.Event.Label, TranscriptTimeout);
        try
        {
            await SendPageAsync(page.Event, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send ntfy notification");
        }
    }

    private async Task SendPageAsync(RadioEvent e, string? transcript)
    {
        // Settings may have changed while waiting for the transcript.
        var config = await LoadConfigAsync();
        if (config == null) return;

        var body = string.IsNullOrWhiteSpace(transcript) ? FormatMessage(e) : transcript.Trim();
        var (ok, message) = await PublishAsync(config, $"Tone out: {e.Label}", body, "rotating_light");
        if (!ok) _logger.LogWarning("ntfy notification for {Label} failed: {Message}", e.Label, message);
    }

    /// <summary>
    /// Sends a sample notification with the saved settings (ignores the enabled switch so it
    /// can be tested before turning on). Returns whether it was accepted and a status message.
    /// </summary>
    public async Task<(bool ok, string message)> SendTestAsync()
    {
        var config = await LoadConfigAsync(requireEnabled: false);
        if (config == null) return (false, "No topic configured");
        return await PublishAsync(config, "OpenScanner test", "ntfy notifications are working.", "white_check_mark");
    }

    internal static string FormatMessage(RadioEvent e)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(e.AlphaTag)) parts.Add(e.AlphaTag!);
        if (e.Frequency > 0) parts.Add($"{e.Frequency.ToString("F4", CultureInfo.InvariantCulture)} MHz");
        if (e.ToneA is > 0)
        {
            var tones = $"A {e.ToneA.Value.ToString("0.#", CultureInfo.InvariantCulture)} Hz";
            if (e.ToneB is > 0) tones += $" / B {e.ToneB.Value.ToString("0.#", CultureInfo.InvariantCulture)} Hz";
            parts.Add(tones);
        }
        if (DateTimeOffset.TryParse(e.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
            parts.Add(ts.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        return parts.Count > 0 ? string.Join(" · ", parts) : e.Label;
    }

    private record NtfyConfig(string ServerUrl, string Topic, string? Token, int Priority);

    private async Task<NtfyConfig?> LoadConfigAsync(bool requireEnabled = true)
    {
        if (requireEnabled && await _db.GetSettingAsync("NtfyEnabled") != "true") return null;

        var topic = (await _db.GetSettingAsync("NtfyTopic"))?.Trim();
        if (string.IsNullOrEmpty(topic)) return null;

        var server = (await _db.GetSettingAsync("NtfyServerUrl"))?.Trim();
        if (string.IsNullOrEmpty(server)) server = DefaultServerUrl;

        var token = (await _db.GetSettingAsync("NtfyToken"))?.Trim();
        var priority = int.TryParse(await _db.GetSettingAsync("NtfyPriority"), out var p) && p is >= 1 and <= 5
            ? p : DefaultPriority;

        return new NtfyConfig(server.TrimEnd('/'), topic, string.IsNullOrEmpty(token) ? null : token, priority);
    }

    // Uses ntfy's JSON publish API (POST to the server root) rather than headers, so
    // non-ASCII tone set names survive intact.
    private async Task<(bool ok, string message)> PublishAsync(NtfyConfig config, string title, string message, string tag)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, config.ServerUrl)
            {
                Content = JsonContent.Create(new
                {
                    topic = config.Topic,
                    title,
                    message,
                    priority = config.Priority,
                    tags = new[] { tag },
                }),
            };
            if (config.Token != null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);

            var client = _httpClientFactory.CreateClient();
            client.Timeout = RequestTimeout;

            using var resp = await client.SendAsync(request);
            if (resp.IsSuccessStatusCode) return (true, $"Sent to {config.Topic}");

            var body = await resp.Content.ReadAsStringAsync();
            return (false, $"HTTP {(int)resp.StatusCode}: {body.Trim()}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
