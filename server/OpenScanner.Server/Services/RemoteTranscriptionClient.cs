using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenScanner.Server.Interfaces;

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
}
