using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests.Services;

public class NtfyNotificationServiceTests
{
    private readonly Mock<IRadioSource> _radio = new();
    private readonly Mock<ITranscriptionService> _transcription = new();
    private readonly Dictionary<string, string> _settings = new()
    {
        ["NtfyEnabled"] = "true",
        ["NtfyTopic"] = "station-pager",
    };
    private readonly FakeTimeProvider _time = new();
    private readonly RecordingHandler _handler = new();
    private readonly NtfyNotificationService _service;

    public NtfyNotificationServiceTests()
    {
        var db = new Mock<IDatabase>();
        db.Setup(d => d.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => _settings.TryGetValue(key, out var v) ? v : null);

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_handler, disposeHandler: false));

        _service = new NtfyNotificationService(_radio.Object, _transcription.Object, db.Object, factory.Object, _time,
            NullLogger<NtfyNotificationService>.Instance);
    }

    private static RadioEvent ToneOut(string label = "Station 1") => new()
    {
        Type = "TONE_OUT",
        Label = label,
        Frequency = 154.3025,
        AlphaTag = "Fire Dispatch",
        ToneA = 525.5,
        ToneB = 1092.4,
        Timestamp = "2026-10-24T12:00:00Z",
        TransmissionId = "log_1000",
    };

    private static CallLog Transcribed(string? text, string id = "log_1000", string? detectedTone = null) =>
        new(id, "2026-10-24T12:00:00Z", 154.3025, "Fire Dispatch", "", null, null, $"{id}.mp3", 20,
            text, detectedTone: detectedTone);

    // Raise a tone-out and complete its recording's transcription.
    private async Task PageAsync(string label = "Station 1", string? transcript = "Engine 3 respond to 12 Main St")
    {
        await _service.HandleEventAsync(ToneOut(label));
        await _service.HandleTranscriptionAsync(Transcribed(transcript));
    }

    private static string Message(CapturedRequest req)
    {
        using var json = JsonDocument.Parse(req.Body);
        return json.RootElement.GetProperty("message").GetString()!;
    }

    [Fact]
    public async Task ToneOut_WaitsForTranscriptAndUsesItAsBody()
    {
        await _service.HandleEventAsync(ToneOut());
        Assert.Empty(_handler.Requests);

        await _service.HandleTranscriptionAsync(Transcribed("  Engine 3 respond to 12 Main St  "));

        var req = Assert.Single(_handler.Requests);
        Assert.Equal("https://ntfy.sh/", req.Uri.ToString());
        Assert.Null(req.Authorization);

        using var json = JsonDocument.Parse(req.Body);
        Assert.Equal("station-pager", json.RootElement.GetProperty("topic").GetString());
        Assert.Equal("Tone out: Station 1", json.RootElement.GetProperty("title").GetString());
        Assert.Equal(5, json.RootElement.GetProperty("priority").GetInt32());
        Assert.Equal("Engine 3 respond to 12 Main St", json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task MatchesByDetectedTone_WhenEventHasNoTransmission()
    {
        var e = ToneOut();
        e.TransmissionId = null;
        await _service.HandleEventAsync(e);

        await _service.HandleTranscriptionAsync(Transcribed("unrelated call", id: "log_2"));
        Assert.Empty(_handler.Requests);

        await _service.HandleTranscriptionAsync(Transcribed("Medic 1 respond", id: "log_3", detectedTone: "Station 1"));
        Assert.Equal("Medic 1 respond", Message(Assert.Single(_handler.Requests)));
    }

    [Fact]
    public async Task BlankTranscript_FallsBackToToneDetails()
    {
        await PageAsync(transcript: null);

        var message = Message(Assert.Single(_handler.Requests));
        Assert.Contains("Fire Dispatch", message);
        Assert.Contains("154.3025 MHz", message);
        Assert.Contains("A 525.5 Hz / B 1092.4 Hz", message);
    }

    [Fact]
    public async Task NoTranscript_SendsDetailsAfterTimeout()
    {
        await _service.HandleEventAsync(ToneOut());
        _time.Advance(NtfyNotificationService.TranscriptTimeout);

        for (int i = 0; i < 100 && _handler.Requests.Count == 0; i++) await Task.Delay(20);
        Assert.Contains("154.3025 MHz", Message(Assert.Single(_handler.Requests)));

        // A late transcript doesn't send a second push.
        await _service.HandleTranscriptionAsync(Transcribed("late"));
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Transcript_CancelsTimeoutFallback()
    {
        await PageAsync();
        _time.Advance(NtfyNotificationService.TranscriptTimeout);
        await Task.Delay(100);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task CustomServerAndToken_AreUsed()
    {
        _settings["NtfyServerUrl"] = "https://ntfy.example.com/";
        _settings["NtfyToken"] = "tk_secret";
        _settings["NtfyPriority"] = "4";

        await PageAsync();

        var req = Assert.Single(_handler.Requests);
        Assert.Equal("https://ntfy.example.com/", req.Uri.ToString());
        Assert.Equal("Bearer tk_secret", req.Authorization);
        using var json = JsonDocument.Parse(req.Body);
        Assert.Equal(4, json.RootElement.GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task NonToneEvent_IsIgnored()
    {
        await _service.HandleEventAsync(new RadioEvent { Type = "MDC_PTT", Label = "Unit 12", TransmissionId = "log_1000" });
        await _service.HandleTranscriptionAsync(Transcribed("text"));
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData("NtfyEnabled", "false")]
    [InlineData("NtfyTopic", "")]
    public async Task DisabledOrNoTopic_SendsNothing(string key, string value)
    {
        _settings[key] = value;
        await PageAsync();
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task RepeatWithinCooldown_IsSuppressedPerToneSet()
    {
        await _service.HandleEventAsync(ToneOut("Station 1"));
        await _service.HandleEventAsync(ToneOut("Station 1"));
        await _service.HandleEventAsync(ToneOut("Station 2"));
        await _service.HandleTranscriptionAsync(Transcribed("both stations"));
        Assert.Equal(2, _handler.Requests.Count);

        _time.Advance(NtfyNotificationService.Cooldown);
        await PageAsync("Station 1");
        Assert.Equal(3, _handler.Requests.Count);
    }

    [Fact]
    public async Task HttpFailure_IsSwallowed()
    {
        _handler.Status = HttpStatusCode.InternalServerError;
        await PageAsync();

        _handler.Throw = true;
        await PageAsync("Station 2");
    }

    [Fact]
    public async Task SendTest_IgnoresEnabledSwitchAndReportsStatus()
    {
        _settings["NtfyEnabled"] = "false";
        var (ok, _) = await _service.SendTestAsync();
        Assert.True(ok);
        Assert.Single(_handler.Requests);

        _handler.Status = HttpStatusCode.Forbidden;
        var (failed, message) = await _service.SendTestAsync();
        Assert.False(failed);
        Assert.Contains("403", message);
    }

    [Fact]
    public async Task SendTest_WithoutTopic_Fails()
    {
        _settings["NtfyTopic"] = "";
        var (ok, _) = await _service.SendTestAsync();
        Assert.False(ok);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Start_SubscribesToRadioAndTranscriptionEvents()
    {
        await _service.StartAsync(CancellationToken.None);
        _radio.Raise(r => r.OnNewEvent += null, ToneOut());
        await Task.Delay(100);
        _transcription.Raise(t => t.OnTranscriptionCompleted += null, Transcribed("Engine 3 respond"));

        // Both handlers hand off to the thread pool; wait for the push to land.
        for (int i = 0; i < 100 && _handler.Requests.Count == 0; i++) await Task.Delay(20);
        Assert.Single(_handler.Requests);
    }

    private record CapturedRequest(Uri Uri, string? Authorization, string Body);

    private class RecordingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Throw) throw new HttpRequestException("network down");
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add(new CapturedRequest(request.RequestUri!, request.Headers.Authorization?.ToString(), body));
            return new HttpResponseMessage(Status) { Content = new StringContent("") };
        }
    }
}
