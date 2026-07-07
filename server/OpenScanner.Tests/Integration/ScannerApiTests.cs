using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace OpenScanner.Tests;

/// <summary>
/// A WebApplicationFactory that points the app at a throwaway SQLite file so the
/// integration tests never touch the developer's real database.
/// </summary>
public class TempDbWebAppFactory : WebApplicationFactory<Program>, IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"openscanner_it_{Guid.NewGuid():N}.db");

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(cfg =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={_dbPath}"
            });
        });
        return base.CreateHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }
}

public class ScannerApiTests : IClassFixture<TempDbWebAppFactory>
{
    private readonly HttpClient _client;

    public ScannerApiTests(TempDbWebAppFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Channels_CrudRoundTrip()
    {
        var freq = 151.1375; // unlikely to collide with seed data

        // Create
        var create = await _client.PostAsJsonAsync("/api/channels", new
        {
            frequency = freq,
            alphaTag = "IntTest",
            description = "integration",
            mode = "NFM"
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();
        Assert.True(id > 0);

        // Read
        var list = await _client.GetFromJsonAsync<JsonElement>("/api/channels");
        Assert.Contains(list.EnumerateArray(), c => c.GetProperty("frequency").GetDouble() == freq);

        // Update
        var update = await _client.PutAsJsonAsync($"/api/channels/{id}", new
        {
            id,
            frequency = freq,
            alphaTag = "IntTestUpdated",
            description = "integration",
            mode = "NFM"
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        // Delete
        var delete = await _client.DeleteAsync($"/api/channels/{id}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        var after = await _client.GetFromJsonAsync<JsonElement>("/api/channels");
        Assert.DoesNotContain(after.EnumerateArray(), c => c.GetProperty("frequency").GetDouble() == freq);
    }

    [Fact]
    public async Task GetScannerStatus_ReturnsState()
    {
        var state = await _client.GetFromJsonAsync<JsonElement>("/api/scanner");
        Assert.True(state.TryGetProperty("status", out var status));
        Assert.False(string.IsNullOrEmpty(status.GetString()));
    }

    [Fact]
    public async Task SetSquelch_Returns200()
    {
        var res = await _client.PutAsJsonAsync("/api/scanner/squelch", new { value = -42.0 });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Hold_PutThenDelete_Succeed()
    {
        var hold = await _client.PutAsJsonAsync("/api/scanner/hold", new { frequency = 155.55 });
        Assert.Equal(HttpStatusCode.OK, hold.StatusCode);

        var release = await _client.DeleteAsync("/api/scanner/hold");
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
    }

    [Fact]
    public async Task AddAvoid_Returns202()
    {
        var res = await _client.PostAsJsonAsync("/api/scanner/avoids", new { frequency = 154.4, duration = 5.0 });
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
    }

    [Fact]
    public async Task SetPower_Off_Returns200()
    {
        var res = await _client.PutAsJsonAsync("/api/scanner/power", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task LegacyControl_Stop_Returns200_WithDeprecationHeader()
    {
        var res = await _client.PostAsJsonAsync("/api/control", new { action = "stop" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Headers.Contains("Deprecation"), "legacy control endpoint should advertise deprecation");
    }

    [Fact]
    public async Task LegacyControl_UnknownAction_Returns400()
    {
        var res = await _client.PostAsJsonAsync("/api/control", new { action = "bogus" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
