using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests.Services;

public class IncidentAnalysisServiceTests
{
    private const string Url = "http://whisper.test:8099";

    private const string SwaggerWithTransmission = """{"paths":{"/transcribe":{"post":{}},"/transmission":{"post":{}}}}""";
    private const string SwaggerWithout = """{"paths":{"/transcribe":{"post":{}}}}""";

    private const string FireResponse = """
        {"callType":"fire","service":"fire","summary":"Structure fire at 45 Main Street","incidentStatus":"dispatched",
         "location":{"address":"45 Main Street","houseNumber":"45","street":"Main Street","city":null,"crossStreet":null,"placeName":null,
           "geocode":{"matchedAddress":"45 MAIN ST, SALEM, NH, 03079","latitude":42.782413,"longitude":-71.233379}}}
        """;

    private readonly FakeHandler _handler = new();
    private readonly Mock<IDatabase> _db = new();
    private readonly Mock<ITranscriptionService> _transcription = new();
    private readonly Dictionary<string, string?> _settings = new()
    {
        ["AiFeaturesEnabled"] = "true",
        ["RemoteTranscriptionUrl"] = Url,
    };
    private readonly List<TransmissionInsight> _saved = new();

    private IncidentAnalysisService CreateService(out RemoteTranscriptionClient remote)
    {
        _db.Setup(d => d.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string k) => _settings.GetValueOrDefault(k));
        _db.Setup(d => d.SaveInsightAsync(It.IsAny<TransmissionInsight>()))
            .Callback<TransmissionInsight>(i => _saved.Add(i)).Returns(Task.CompletedTask);

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_handler, disposeHandler: false));
        var config = new ConfigurationBuilder().Build();
        remote = new RemoteTranscriptionClient(factory.Object, _db.Object, config, NullLogger<RemoteTranscriptionClient>.Instance);
        return new IncidentAnalysisService(_transcription.Object, _db.Object, remote, NullLogger<IncidentAnalysisService>.Instance);
    }

    private static CallLog Log(string text = "Engine 2 respond to 45 Main Street for a structure fire") =>
        new("tx1", DateTime.UtcNow.ToString("o"), 154.43, "Salem FD", "Fire dispatch", null, null, "tx1.raw", 4, text);

    [Fact]
    public async Task HasTransmissionRoute_TrueOnlyWhenSwaggerListsIt()
    {
        CreateService(out var remote);
        _handler.Responses["/swagger/v1/swagger.json"] = (HttpStatusCode.OK, SwaggerWithTransmission);
        Assert.True(await remote.HasTransmissionRouteAsync(Url));

        _handler.Responses["/swagger/v1/swagger.json"] = (HttpStatusCode.OK, SwaggerWithout);
        Assert.False(await remote.HasTransmissionRouteAsync("http://other.test"));
    }

    [Fact]
    public async Task HasTransmissionRoute_FalseWhenServerUnreachable()
    {
        CreateService(out var remote);
        _handler.Throw = true;
        Assert.False(await remote.HasTransmissionRouteAsync(Url));
    }

    [Fact]
    public async Task Analyze_IncidentIsStoredAttachedAndRaised()
    {
        var service = CreateService(out _);
        _handler.Responses["/swagger/v1/swagger.json"] = (HttpStatusCode.OK, SwaggerWithTransmission);
        _handler.Responses["/transmission"] = (HttpStatusCode.OK, FireResponse);
        CallLog? raised = null;
        service.OnInsightReady += l => raised = l;

        var log = Log();
        var insight = await service.AnalyzeAsync(log, CancellationToken.None);

        Assert.NotNull(insight);
        Assert.True(insight!.IsIncident);
        Assert.Equal("tx1", insight.TransmissionId);
        Assert.Equal("fire", insight.CallType);
        Assert.Equal("dispatched", insight.IncidentStatus);
        Assert.Equal("45 MAIN ST, SALEM, NH, 03079", insight.MatchedAddress);
        Assert.Equal(42.782413, insight.Lat);
        Assert.Equal(-71.233379, insight.Lon);
        Assert.Single(_saved);
        Assert.Same(log, raised);
        Assert.Same(insight, log.Insight);

        // The channel name says fire, so the request carries the service hint.
        var body = JsonDocument.Parse(_handler.Bodies["/transmission"]).RootElement;
        Assert.Equal("fire", body.GetProperty("service").GetString());
        Assert.Equal(log.Transcription, body.GetProperty("text").GetString());
        // No default locality configured: sent as null so the server uses its own default.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("defaultCity").ValueKind);
    }

    [Fact]
    public async Task Analyze_SendsTheChannelsCityAndState()
    {
        _db.Setup(d => d.GetChannelByFrequencyAsync(154.43))
            .ReturnsAsync(new Channel(154.43, "Salem FD", "Fire dispatch") { City = " Salem ", State = "NH" });
        var service = CreateService(out _);
        _handler.Responses["/swagger/v1/swagger.json"] = (HttpStatusCode.OK, SwaggerWithTransmission);
        _handler.Responses["/transmission"] = (HttpStatusCode.OK, FireResponse);

        await service.AnalyzeAsync(Log(), CancellationToken.None);

        var body = JsonDocument.Parse(_handler.Bodies["/transmission"]).RootElement;
        Assert.Equal("Salem", body.GetProperty("defaultCity").GetString());
        Assert.Equal("NH", body.GetProperty("defaultState").GetString());
    }

    [Fact]
    public async Task Analyze_BlankChannelLocalityIsSentAsNull()
    {
        _db.Setup(d => d.GetChannelByFrequencyAsync(It.IsAny<double>()))
            .ReturnsAsync(new Channel(154.43, "Salem FD", "Fire dispatch") { City = "  ", State = null });
        var service = CreateService(out _);
        _handler.Responses["/swagger/v1/swagger.json"] = (HttpStatusCode.OK, SwaggerWithTransmission);
        _handler.Responses["/transmission"] = (HttpStatusCode.OK, FireResponse);

        await service.AnalyzeAsync(Log(), CancellationToken.None);

        var body = JsonDocument.Parse(_handler.Bodies["/transmission"]).RootElement;
        Assert.Equal(JsonValueKind.Null, body.GetProperty("defaultCity").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("defaultState").ValueKind);
    }

    [Fact]
    public async Task Analyze_JoinsANearbyIncidentFromTheSameService()
    {
        var service = CreateService(out _);
        _handler.Responses["/swagger/v1/swagger.json"] = (HttpStatusCode.OK, SwaggerWithTransmission);
        _handler.Responses["/transmission"] = (HttpStatusCode.OK, FireResponse);
        var log = Log();
        _db.Setup(d => d.FindIncidentCandidatesAsync("fire", It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<string>(), It.IsAny<string>(), "tx1"))
            .ReturnsAsync(new[]
            {
                new IncidentCandidate { TransmissionId = "tx0", IncidentId = "first", Lat = 42.7825, Lon = -71.2334, Timestamp = DateTime.UtcNow.AddMinutes(-4).ToString("o") },
            });

        var insight = await service.AnalyzeAsync(log, CancellationToken.None);

        Assert.Equal("first", insight!.IncidentId);
        Assert.Equal("first", _saved.Single().IncidentId);
    }

    [Fact]
    public async Task Analyze_StartsANewIncidentWhenNothingIsNearby()
    {
        var service = CreateService(out _);
        _handler.Responses["/swagger/v1/swagger.json"] = (HttpStatusCode.OK, SwaggerWithTransmission);
        _handler.Responses["/transmission"] = (HttpStatusCode.OK, FireResponse);

        var insight = await service.AnalyzeAsync(Log(), CancellationToken.None);

        Assert.Equal("tx1", insight!.IncidentId);
    }

    [Fact]
    public async Task GroupUngrouped_AssignsEachEarlierIncident()
    {
        var service = CreateService(out _);
        var t0 = DateTime.UtcNow.AddHours(-1);
        _db.Setup(d => d.GetUngroupedIncidentsAsync()).ReturnsAsync(new[]
        {
            new UngroupedIncident { TransmissionId = "a", Service = "fire", Lat = 42.78, Lon = -71.23, Timestamp = t0.ToString("o") },
            new UngroupedIncident { TransmissionId = "b", Service = "fire", Lat = 42.78, Lon = -71.23, Timestamp = t0.AddMinutes(3).ToString("o") },
        });
        // "b" finds "a", which was grouped just before it.
        _db.Setup(d => d.FindIncidentCandidatesAsync(It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<string>(), It.IsAny<string>(), "b"))
            .ReturnsAsync(new[] { new IncidentCandidate { TransmissionId = "a", IncidentId = "a", Lat = 42.78, Lon = -71.23, Timestamp = t0.ToString("o") } });
        var assigned = new Dictionary<string, string>();
        _db.Setup(d => d.SetIncidentIdAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((tx, id) => assigned[tx] = id).Returns(Task.CompletedTask);

        Assert.Equal(2, await service.GroupUngroupedAsync(CancellationToken.None));
        Assert.Equal("a", assigned["a"]);
        Assert.Equal("a", assigned["b"]);
    }

    [Fact]
    public async Task Analyze_RoutineChatterIsStoredButNotRaised()
    {
        var service = CreateService(out _);
        _handler.Responses["/swagger/v1/swagger.json"] = (HttpStatusCode.OK, SwaggerWithTransmission);
        _handler.Responses["/transmission"] = (HttpStatusCode.OK, "{}");
        var raised = false;
        service.OnInsightReady += _ => raised = true;

        var insight = await service.AnalyzeAsync(Log("ten four copy that"), CancellationToken.None);

        Assert.NotNull(insight);
        Assert.False(insight!.IsIncident);
        Assert.Single(_saved);
        Assert.False(raised);
    }

    [Fact]
    public async Task Analyze_ServerErrorStoresNothing()
    {
        var service = CreateService(out _);
        _handler.Responses["/swagger/v1/swagger.json"] = (HttpStatusCode.OK, SwaggerWithTransmission);
        _handler.Responses["/transmission"] = (HttpStatusCode.BadGateway, """{"error":"llm down"}""");

        Assert.Null(await service.AnalyzeAsync(Log(), CancellationToken.None));
        Assert.Empty(_saved);
    }

    [Theory]
    [InlineData("false", Url)]
    [InlineData("true", null)]
    public async Task Analyze_DisabledOrNoServer_MakesNoCall(string enabled, string? url)
    {
        _settings["AiFeaturesEnabled"] = enabled;
        _settings["RemoteTranscriptionUrl"] = url;
        var service = CreateService(out _);

        Assert.Null(await service.AnalyzeAsync(Log(), CancellationToken.None));
        Assert.Empty(_handler.Paths);
    }

    [Fact]
    public async Task Analyze_UnsupportedServer_DoesNotPostTranscript()
    {
        var service = CreateService(out _);
        _handler.Responses["/swagger/v1/swagger.json"] = (HttpStatusCode.OK, SwaggerWithout);

        Assert.Null(await service.AnalyzeAsync(Log(), CancellationToken.None));
        Assert.DoesNotContain("/transmission", _handler.Paths);
    }

    [Fact]
    public void ParseTransmission_DropsMissingOrZeroCoordinates()
    {
        var json = JsonDocument.Parse("""{"callType":"medical","location":{"address":"Main St","geocode":{"latitude":0,"longitude":0}}}""").RootElement;
        var insight = RemoteTranscriptionClient.ParseTransmission(json);
        Assert.True(insight.IsIncident);
        Assert.Null(insight.Lat);
        Assert.Null(insight.Lon);

        var noGeo = RemoteTranscriptionClient.ParseTransmission(JsonDocument.Parse("""{"callType":"theft","location":{"geocode":null}}""").RootElement);
        Assert.True(noGeo.IsIncident);
        Assert.Null(noGeo.Lat);
    }

    [Theory]
    [InlineData("Salem FD", "", "fire")]
    [InlineData("County EMS", "", "fire")]
    [InlineData("Salem PD", "Police dispatch", "police")]
    [InlineData("Sheriff Ops", "", "police")]
    [InlineData("Town Ops", "Public works", null)]
    [InlineData("Fire/PD Interop", "", null)]
    public void ServiceHint_FromChannelName(string alphaTag, string description, string? expected)
    {
        Assert.Equal(expected, IncidentAnalysisService.ServiceHint(alphaTag, description));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("copy", false)]
    [InlineData("copy that", true)]
    public void IsWorthAnalyzing_NeedsTwoWords(string? text, bool expected)
    {
        Assert.Equal(expected, IncidentAnalysisService.IsWorthAnalyzing(text));
    }

    [Fact]
    public async Task Dispose_IsSafeToCallTwice()
    {
        // DI disposes it twice: once as the singleton, once as the hosted service.
        var service = CreateService(out _);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        service.Dispose();
        service.Dispose();
        await service.StopAsync(CancellationToken.None);
    }

    private class FakeHandler : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Body)> Responses { get; } = new();
        public Dictionary<string, string> Bodies { get; } = new();
        public List<string> Paths { get; } = new();
        public bool Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Throw) throw new HttpRequestException("network down");
            var path = request.RequestUri!.AbsolutePath;
            lock (Paths) Paths.Add(path);
            if (request.Content != null) Bodies[path] = await request.Content.ReadAsStringAsync(ct);
            var (status, body) = Responses.TryGetValue(path, out var r) ? r : (HttpStatusCode.NotFound, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
