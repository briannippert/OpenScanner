using Microsoft.AspNetCore.Mvc;
using Moq;
using OpenScanner.Server.Controllers;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using Xunit;

namespace OpenScanner.Tests.Controllers;

public class TransmissionsControllerTests
{
    private readonly Mock<IDatabase> _db = new();
    private readonly TransmissionsController _controller;
    private TransmissionQuery? _captured;

    public TransmissionsControllerTests()
    {
        _db.Setup(d => d.QueryTransmissionsAsync(It.IsAny<TransmissionQuery>()))
            .Callback<TransmissionQuery>(q => _captured = q)
            .ReturnsAsync(new TransmissionPage());
        _controller = new TransmissionsController(_db.Object);
    }

    private Task<IActionResult> Get(int? limit = null, string? cursor = null, string? since = null, string? until = null,
        string? channel = null, string? q = null, bool favorites = false, bool tones = false, string? ids = null) =>
        _controller.Get(limit, cursor, since, until, channel, null, q, favorites, tones, false, ids);

    [Theory]
    [InlineData(null, 50)]
    [InlineData(10, 10)]
    [InlineData(0, 1)]
    [InlineData(5000, 200)]
    public async Task Limit_DefaultsAndClamps(int? requested, int expected)
    {
        Assert.IsType<OkObjectResult>(await Get(limit: requested));
        Assert.Equal(expected, _captured!.Limit);
    }

    [Fact]
    public async Task Cursor_RoundTripsToTheQuery()
    {
        var cursor = new TransmissionCursor("2026-11-01T12:00:00.0000000Z", "log_1").Encode();

        await Get(cursor: cursor);

        Assert.Equal(new TransmissionCursor("2026-11-01T12:00:00.0000000Z", "log_1"), _captured!.Cursor);
    }

    [Theory]
    [InlineData("not-a-cursor!!")]
    [InlineData("bm9waXBl")] // base64url of "nopipe"
    public async Task MalformedCursor_IsRejected(string cursor)
    {
        Assert.IsType<BadRequestObjectResult>(await Get(cursor: cursor));
        _db.Verify(d => d.QueryTransmissionsAsync(It.IsAny<TransmissionQuery>()), Times.Never);
    }

    [Fact]
    public async Task BadDates_AreRejected()
    {
        Assert.IsType<BadRequestObjectResult>(await Get(since: "yesterday-ish"));
        Assert.IsType<BadRequestObjectResult>(await Get(until: "nope"));
    }

    [Fact]
    public async Task Dates_AreNormalizedToRoundTripUtc()
    {
        await Get(since: "2026-11-01T08:00:00-04:00", until: "2026-11-02");

        Assert.Equal("2026-11-01T12:00:00.0000000Z", _captured!.SinceUtc);
        Assert.Equal("2026-11-02T00:00:00.0000000Z", _captured.UntilUtc);
    }

    [Fact]
    public async Task Filters_ArePassedThrough()
    {
        await Get(channel: "FD Dispatch", q: "  engine ", favorites: true, tones: true);

        Assert.Equal("FD Dispatch", _captured!.Channel);
        Assert.Equal("engine", _captured.Search);
        Assert.True(_captured.FavoritesOnly);
        Assert.True(_captured.TonesOnly);
        Assert.False(_captured.IncidentsOnly);
    }

    [Fact]
    public async Task Ids_AreSplitAndDeduplicated()
    {
        await Get(ids: "a, b,,a");

        Assert.Equal(new[] { "a", "b" }, _captured!.Ids);
    }

    [Fact]
    public async Task TooManyIds_AreRejected()
    {
        var ids = string.Join(',', Enumerable.Range(0, 201).Select(i => $"id{i}"));

        Assert.IsType<BadRequestObjectResult>(await Get(ids: ids));
    }
}
