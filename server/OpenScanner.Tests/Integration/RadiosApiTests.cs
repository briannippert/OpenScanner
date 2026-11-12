using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace OpenScanner.Tests;

/// <summary>The app on a throwaway database with two simulated dongles.</summary>
public class TwoMockSdrsFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"openscanner_radios_{Guid.NewGuid():N}.db");

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = $"Data Source={_dbPath}",
            ["Radio:Provider"] = "Mock",
            ["Radio:MockDeviceCount"] = "2",
        }));
        return base.CreateHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }
}

public class RadiosApiTests : IClassFixture<TwoMockSdrsFactory>
{
    private readonly HttpClient _client;

    public RadiosApiTests(TwoMockSdrsFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<JsonElement> Radios()
    {
        // The first listing happens in the background at startup; rescan makes it deterministic.
        var res = await _client.PostAsync("/api/radios/rescan", null);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task ListsTheAttachedDongles()
    {
        var radios = await Radios();

        var ids = radios.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("id").GetString()).ToList();
        Assert.Contains("sn:MOCK0000", ids);
        Assert.Contains("sn:MOCK0001", ids);
        Assert.All(radios.GetProperty("devices").EnumerateArray(), d => Assert.True(d.GetProperty("attached").GetBoolean()));
    }

    [Fact]
    public async Task UpdatesADonglesSettings()
    {
        await Radios();

        var res = await _client.PutAsJsonAsync("/api/radios/sn:MOCK0001", new { label = "Roof", gainDigital = 28.0 });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var device = (await Radios()).GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("id").GetString() == "sn:MOCK0001");
        Assert.Equal("Roof", device.GetProperty("label").GetString());
        Assert.Equal(28.0, device.GetProperty("gainDigital").GetDouble());
    }

    [Theory]
    [InlineData("{\"gainAnalog\": 99}")]
    [InlineData("{\"ppm\": -1000}")]
    public async Task RejectsOutOfRangeTuning(string body)
    {
        await Radios();
        var res = await _client.PutAsync("/api/radios/sn:MOCK0000",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task UnknownDongle_IsNotFound()
    {
        await Radios();
        var res = await _client.PutAsJsonAsync("/api/radios/sn:NOPE", new { label = "x" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task SwitchesMode()
    {
        var res = await _client.PutAsJsonAsync("/api/radios/mode", new { mode = "manual" });
        Assert.Equal("manual", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mode").GetString());

        res = await _client.PutAsJsonAsync("/api/radios/mode", new { mode = "auto" });
        Assert.Equal("auto", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mode").GetString());

        res = await _client.PutAsJsonAsync("/api/radios/mode", new { mode = "sideways" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task PinsChannels_AndPreviewsWithoutApplying()
    {
        await Radios();
        var a = await (await _client.PostAsJsonAsync("/api/channels", new { frequency = 153.0101, alphaTag = "PinA", mode = "NFM" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var b = await (await _client.PostAsJsonAsync("/api/channels", new { frequency = 153.0202, alphaTag = "PinB", mode = "NFM" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        int idA = a.GetProperty("id").GetInt32(), idB = b.GetProperty("id").GetInt32();

        // Preview: B on the second SDR in Manual mode.
        var preview = await (await _client.PostAsJsonAsync("/api/radios/plan/preview",
            new { mode = "manual", pins = new Dictionary<string, string?> { [idB.ToString()] = "sn:MOCK0001" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var owner = preview.GetProperty("devices").EnumerateArray()
            .Single(d => d.GetProperty("channelIds").EnumerateArray().Any(i => i.GetInt32() == idB));
        Assert.Equal("sn:MOCK0001", owner.GetProperty("deviceId").GetString());

        // Pin for real.
        var res = await _client.PutAsJsonAsync("/api/radios/assignments", new { channelIds = new[] { idA, idB }, deviceId = "sn:MOCK0000" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var radios = await res.Content.ReadFromJsonAsync<JsonElement>();
        var pinnedTo = radios.GetProperty("plan").GetProperty("devices").EnumerateArray()
            .Single(d => d.GetProperty("channelIds").EnumerateArray().Any(i => i.GetInt32() == idA));
        Assert.NotNull(pinnedTo.GetProperty("deviceId").GetString());
        var channels = await _client.GetFromJsonAsync<JsonElement>("/api/channels");
        Assert.Equal("sn:MOCK0000", channels.EnumerateArray().Single(c => c.GetProperty("id").GetInt32() == idA).GetProperty("sdrDeviceId").GetString());

        res = await _client.PutAsJsonAsync("/api/radios/assignments", new { channelIds = new[] { idA }, deviceId = "sn:NOPE" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
