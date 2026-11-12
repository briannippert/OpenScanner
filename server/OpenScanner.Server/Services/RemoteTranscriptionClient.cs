using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

/// <summary>A model advertised by a remote OpenScanner.WhisperServer.</summary>
public record RemoteModel(string Id, string Label);

/// <summary>
/// Talks to a remote <c>OpenScanner.WhisperServer</c> over HTTP so transcription can be
/// offloaded from the Pi. The base URL is read from the <c>RemoteTranscriptionUrl</c> setting
/// for the live transcribe path; the model/health probes take an explicit URL so the settings
/// UI can test a server before the value is saved.
/// </summary>
public class RemoteTranscriptionClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDatabase _db;
    private readonly IConfiguration _config;
    private readonly ILogger<RemoteTranscriptionClient> _logger;

    public RemoteTranscriptionClient(IHttpClientFactory httpClientFactory, IDatabase db, IConfiguration config, ILogger<RemoteTranscriptionClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _db = db;
        _config = config;
        _logger = logger;
    }

    private int TimeoutSeconds =>
        int.TryParse(_config["Transcription:RemoteTimeoutSeconds"], out var t) && t > 0 ? t : 120;

    private static string Normalize(string url) => url.TrimEnd('/');

    /// <summary>The configured remote server base URL, or null when not set.</summary>
    public async Task<string?> GetConfiguredUrlAsync()
    {
        var url = await _db.GetSettingAsync("RemoteTranscriptionUrl");
        return string.IsNullOrWhiteSpace(url) ? null : Normalize(url);
    }

    /// <summary>
    /// Send a (16 kHz mono) WAV to the configured remote server and return the transcript.
    /// Returns null on any failure or empty/blank result so the caller can fall through.
    /// </summary>
    public async Task<string?> TranscribeAsync(string wavPath, string model, string prompt, CancellationToken ct = default)
    {
        var baseUrl = await GetConfiguredUrlAsync();
        if (baseUrl == null)
        {
            _logger.LogError("Remote transcription selected but RemoteTranscriptionUrl is not set.");
            return null;
        }

        try
        {
            using var content = new MultipartFormDataContent();
            await using var fs = File.OpenRead(wavPath);
            var fileContent = new StreamContent(fs);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            content.Add(fileContent, "file", Path.GetFileName(wavPath));
            if (!string.IsNullOrWhiteSpace(model)) content.Add(new StringContent(model), "model");
            if (!string.IsNullOrEmpty(prompt)) content.Add(new StringContent(prompt), "prompt");

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);

            using var resp = await client.PostAsync($"{baseUrl}/transcribe", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogError("Remote transcription failed (HTTP {Code}): {Body}", (int)resp.StatusCode, body);
                return null;
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var text = json.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(text)) return null;
            if (text.StartsWith('[') && text.EndsWith(']')) return null;
            return text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling remote transcription server at {Url}", baseUrl);
            return null;
        }
    }

    /// <summary>Query the models installed on a remote server (used to populate the UI dropdown).</summary>
    public async Task<IReadOnlyList<RemoteModel>> GetModelsAsync(string url, CancellationToken ct = default)
    {
        var baseUrl = Normalize(url);
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        using var resp = await client.GetAsync($"{baseUrl}/models", ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        var models = new List<RemoteModel>();
        if (json.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray())
            {
                var id = m.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var label = m.TryGetProperty("label", out var lblProp) ? lblProp.GetString() : null;
                models.Add(new RemoteModel(id!, string.IsNullOrWhiteSpace(label) ? id! : label!));
            }
        }
        return models;
    }

    /// <summary>Probe a remote server's /health endpoint. Returns the raw JSON for the "Test" button.</summary>
    public async Task<JsonElement?> HealthAsync(string url, CancellationToken ct = default)
    {
        var baseUrl = Normalize(url);
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        try
        {
            using var resp = await client.GetAsync($"{baseUrl}/health", ct);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            return json;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health probe failed for {Url}", baseUrl);
            return null;
        }
    }

    // ---- AI call analysis (/transmission) ----

    /// <summary>Setting: default city used to ground address lookups.</summary>
    public const string DefaultCitySetting = "AiDefaultCity";

    /// <summary>Setting: default state used to ground address lookups.</summary>
    public const string DefaultStateSetting = "AiDefaultState";

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The server accepts at most this many characters of text.</summary>
    public const int MaxTransmissionTextLength = 2000;

    private static readonly TimeSpan CapabilityCacheTtl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (bool Supported, DateTime CheckedUtc)> _capabilityCache = new();

    /// <summary>
    /// Whether the server at <paramref name="url"/> offers AI call analysis, detected by
    /// a <c>POST /transmission</c> operation in its OpenAPI document
    /// (<c>/swagger/v1/swagger.json</c>). Cached per URL for a few minutes; any failure
    /// counts as unsupported.
    /// </summary>
    public async Task<bool> HasTransmissionRouteAsync(string url, CancellationToken ct = default)
    {
        var baseUrl = Normalize(url);
        if (_capabilityCache.TryGetValue(baseUrl, out var cached) && DateTime.UtcNow - cached.CheckedUtc < CapabilityCacheTtl)
        {
            return cached.Supported;
        }

        var supported = false;
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            using var resp = await client.GetAsync($"{baseUrl}/swagger/v1/swagger.json", ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                supported = json.ValueKind == JsonValueKind.Object
                    && json.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object
                    && paths.TryGetProperty("/transmission", out var route) && route.ValueKind == JsonValueKind.Object
                    && route.TryGetProperty("post", out _);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Capability probe failed for {Url}", baseUrl);
        }

        _capabilityCache[baseUrl] = (supported, DateTime.UtcNow);
        return supported;
    }

    /// <summary>
    /// Sends a transcript to the configured server's <c>/transmission</c> endpoint. Returns
    /// an insight with <see cref="TransmissionInsight.IsIncident"/> false for routine chatter,
    /// or null when the server could not be reached or failed, so the caller can retry later.
    /// <see cref="TransmissionInsight.TransmissionId"/> is left for the caller to fill in.
    /// </summary>
    public async Task<TransmissionInsight?> ExtractTransmissionAsync(string text, string? service, CancellationToken ct = default)
    {
        var baseUrl = await GetConfiguredUrlAsync();
        if (baseUrl == null) return null;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim();
        if (trimmed.Length > MaxTransmissionTextLength) trimmed = trimmed[..MaxTransmissionTextLength];

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);
            // Optional locality that grounds geocoding (the server falls back to its own
            // configured default area). A city/state spoken in the call still wins.
            var defaultCity = Blank(await _db.GetSettingAsync(DefaultCitySetting));
            var defaultState = Blank(await _db.GetSettingAsync(DefaultStateSetting));
            var body = JsonSerializer.Serialize(new { text = trimmed, service, defaultCity, defaultState });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync($"{baseUrl}/transmission", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Transmission analysis failed (HTTP {Code}): {Body}", (int)resp.StatusCode, err);
                return null;
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            return ParseTransmission(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error calling transmission analysis at {Url}", baseUrl);
            return null;
        }
    }

    /// <summary>Maps a <c>/transmission</c> response to an insight. <c>{}</c> is routine chatter.</summary>
    internal static TransmissionInsight ParseTransmission(JsonElement json)
    {
        var insight = new TransmissionInsight { AnalyzedAt = DateTime.UtcNow.ToString("o") };
        if (json.ValueKind != JsonValueKind.Object) return insight;

        insight.CallType = Str(json, "callType");
        insight.Service = Str(json, "service");
        insight.Summary = Str(json, "summary");
        insight.IncidentStatus = Str(json, "incidentStatus");

        if (json.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object)
        {
            insight.Address = Str(loc, "address");
            insight.PlaceName = Str(loc, "placeName");
            insight.CrossStreet = Str(loc, "crossStreet");
            insight.City = Str(loc, "city");
            insight.State = Str(loc, "state");
            if (loc.TryGetProperty("geocode", out var geo) && geo.ValueKind == JsonValueKind.Object)
            {
                insight.MatchedAddress = Str(geo, "matchedAddress");
                insight.Lat = Num(geo, "latitude");
                insight.Lon = Num(geo, "longitude");
                // A (0,0) or out-of-range fix is not a real location.
                if (insight.Lat is not { } la || insight.Lon is not { } lo || Math.Abs(la) > 90 || Math.Abs(lo) > 180 || (la == 0 && lo == 0))
                {
                    insight.Lat = insight.Lon = null;
                }
            }
        }

        insight.IsIncident = !string.IsNullOrWhiteSpace(insight.CallType);
        return insight;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
}
