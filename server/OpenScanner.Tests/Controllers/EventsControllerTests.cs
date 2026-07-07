using Microsoft.AspNetCore.Mvc;
using Moq;
using OpenScanner.Server.Controllers;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using Xunit;

namespace OpenScanner.Tests.Controllers;

public class EventsControllerTests
{
    private readonly Mock<IDatabase> _dbMock = new();
    private readonly EventsController _controller;

    public EventsControllerTests()
    {
        _controller = new EventsController(_dbMock.Object);
    }

    [Fact]
    public async Task GetEvents_ReturnsEvents()
    {
        var events = new List<RadioEvent>
        {
            new() { Id = "1", Type = "TONE_OUT", Label = "Station 1" },
            new() { Id = "2", Type = "MDC_PTT", Label = "PTT ID", UnitId = 0x1234 },
        };
        _dbMock.Setup(db => db.GetRadioEventsAsync(100)).ReturnsAsync(events);

        var result = await _controller.GetEvents();

        Assert.Equal(2, result.Count());
    }

    [Fact]
    public async Task ClearEvents_ClearsAndReturnsOk()
    {
        var result = await _controller.ClearEvents() as OkResult;

        Assert.NotNull(result);
        _dbMock.Verify(db => db.ClearRadioEventsAsync(), Times.Once);
    }
}
