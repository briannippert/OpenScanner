using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenScanner.Server.Interfaces;

namespace OpenScanner.Server.Services;

public class RemoteWhisperTranscriptionService : ITranscriptionService
{
    private readonly IDatabase _db;
    private readonly ILogger<RemoteWhisperTranscriptionService> _logger;
    private readonly HttpClient _httpClient;

    // Radio-context prompt (same as local WhisperTranscriptionService)
    private const string RadioPrompt =
        "Dispatch, Unit 1, 10-4, copy, over. Priority traffic, code 3 response to street intersection. " +
        "Suspect description: white male, blue jeans. License plate, vehicle registration, bolo. " +
        "Structure fire, medical emergency, staging area. Status check, affirmative, negative, stand by. " +
        "Channel 2, tac channel, command post. Kilo, Tango, Zulu, X-ray. " +
        "10-20 location, 10-8 in service, 10-7 out of service.";

    public RemoteWhisperTranscriptionService(
        IDatabase db,
        ILogger<RemoteWhisperTranscriptionService> logger,
        HttpClient httpClient)
    {
        _db = db;
        _logger = logger;
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(120);
    }

    public virtual string? TranscribeAudio(string audioPath)
    {
        var enabled = _db.GetSettingAsync("EnableTranscription").GetAwaiter().GetResult();
        if (enabled != "true") return null;

        var serverUrl = _db.GetSettingAsync("TranscriptionServerUrl").GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            _logger.LogError("Remote transcription server URL is not configured");
            return null;
        }

        // Convert audio to 16kHz mono WAV (same preprocessing as local service)
        var tempWavPath = audioPath + ".16k.wav";

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
            convertStart.ArgumentList.Add("48000");
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

        try
        {
            var url = serverUrl.TrimEnd('/') + "/transcribe";

            using var content = new MultipartFormDataContent();
            var fileBytes = File.ReadAllBytes(tempWavPath);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(fileContent, "file", Path.GetFileName(tempWavPath));
            content.Add(new StringContent(RadioPrompt), "prompt");

            var diarize = _db.GetSettingAsync("EnableDiarization").GetAwaiter().GetResult();
            if (diarize == "true")
            {
                content.Add(new StringContent("true"), "diarize");
            }

            var response = _httpClient.PostAsync(url, content).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                _logger.LogError(
                    $"Remote transcription server returned {(int)response.StatusCode}: {errorBody}");
                return null;
            }

            var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement.GetProperty("text").GetString()?.Trim();

            if (string.IsNullOrEmpty(text)) return null;
            if (text.StartsWith("[") && text.EndsWith("]")) return null;

            return text;
        }
        catch (TaskCanceledException)
        {
            _logger.LogError("Remote transcription request timed out");
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to remote transcription server");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during remote transcription");
            return null;
        }
        finally
        {
            if (File.Exists(tempWavPath)) File.Delete(tempWavPath);
        }
    }
}
