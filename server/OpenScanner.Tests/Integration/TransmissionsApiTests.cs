using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using Xunit;

namespace OpenScanner.Tests;

public class TransmissionsApiTests : IClassFixture<TempDbWebAppFactory>
{
    private readonly TempDbWebAppFactory _factory;

    public TransmissionsApiTests(TempDbWebAppFactory factory)
    {
        _factory = factory;
    }

    private async Task SeedAsync(string prefix, int count)
    {
        var db = _factory.Services.GetRequiredService<IDatabase>();
        var t0 = DateTime.UtcNow.AddDays(-1);
        for (var i = 0; i < count; i++)
        {
            await db.SaveTransmissionAsync(new CallLog($"{prefix}_{i:D2}", t0.AddSeconds(i).ToString("o"), 154.0, prefix, "", null, null, null, 1));
        }
    }

    [Fact]
    public async Task Paging_WalksEveryRowOnce()
    {
        await SeedAsync("ItPage", 12);
        var client = _factory.CreateClient();

        var ids = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = "/api/transmissions?channel=ItPage&limit=5" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var doc = JsonDocument.Parse(await client.GetStringAsync(url));
            ids.AddRange(doc.RootElement.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetString()!));
            var next = doc.RootElement.GetProperty("nextCursor");
            cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetString();
            pages++;
        } while (cursor != null);

        Assert.Equal(3, pages);
        Assert.Equal(Enumerable.Range(0, 12).Reverse().Select(i => $"ItPage_{i:D2}"), ids);
    }

    [Fact]
    public async Task BadCursor_Returns400()
    {
        var response = await _factory.CreateClient().GetAsync("/api/transmissions?cursor=%21%21");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LegacyHistory_StillReturnsABareArray()
    {
        await SeedAsync("ItLegacy", 1);

        using var doc = JsonDocument.Parse(await _factory.CreateClient().GetStringAsync("/api/history"));

        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    public async Task JsonResponses_AreCompressedWhenAsked(string encoding)
    {
        await SeedAsync($"ItZip{encoding}", 3);
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/transmissions?limit=3");
        request.Headers.AcceptEncoding.ParseAdd(encoding);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(encoding, response.Content.Headers.ContentEncoding);
    }
}
