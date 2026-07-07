using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using OpenScanner.Server.Controllers;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests.Controllers;

public class FiretonesControllerTests
{
    private readonly Mock<IDatabase> _dbMock;
    private readonly ToneDetector _toneDetector;
    private readonly FiretonesController _controller;

    public FiretonesControllerTests()
    {
        _dbMock = new Mock<IDatabase>();
        _dbMock.Setup(db => db.GetAllFireTonesAsync()).ReturnsAsync(Array.Empty<FireToneSet>());
        _toneDetector = new ToneDetector(_dbMock.Object, new Mock<ILogger<ToneDetector>>().Object);
        _controller = new FiretonesController(_dbMock.Object, _toneDetector);
    }

    [Fact]
    public async Task GetFireTones_ReturnsTones()
    {
        // Arrange
        var tones = new List<FireToneSet> { new FireToneSet { Name = "Station 1" } };
        _dbMock.Setup(db => db.GetAllFireTonesAsync()).ReturnsAsync(tones);

        // Act
        var result = await _controller.GetFireTones();

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public async Task AddFireTone_ReturnsCreated()
    {
        // Arrange
        var tone = new FireToneSet { Name = "New Station" };
        _dbMock.Setup(db => db.AddFireToneAsync(tone)).ReturnsAsync(5);

        // Act
        var result = await _controller.AddFireTone(tone) as CreatedAtActionResult;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(5, ((FireToneSet)result.Value!).Id);
    }

    [Fact]
    public async Task UpdateFireTone_UpdatesTone()
    {
        // Arrange
        var tone = new FireToneSet { Id = 2, Name = "Updated" };

        // Act
        var result = await _controller.UpdateFireTone(2, tone) as OkResult;

        // Assert
        Assert.NotNull(result);
        _dbMock.Verify(db => db.UpdateFireToneAsync(It.Is<FireToneSet>(t => t.Id == 2 && t.Name == "Updated")), Times.Once);
    }

    [Fact]
    public async Task DeleteFireTone_DeletesTone()
    {
        // Act
        var result = await _controller.DeleteFireTone(2) as OkResult;

        // Assert
        Assert.NotNull(result);
        _dbMock.Verify(db => db.DeleteFireToneAsync(2), Times.Once);
    }

    [Fact]
    public async Task AddFireTone_ReloadsDetector()
    {
        // The detector loads tones once in its constructor; a successful add must
        // trigger a reload so the running detector picks up the new tone set.
        _dbMock.Setup(db => db.AddFireToneAsync(It.IsAny<FireToneSet>())).ReturnsAsync(1);

        await _controller.AddFireTone(new FireToneSet { Name = "New Station" });

        // ReloadTones fetches the tone list again (fire-and-forget); poll for it.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                _dbMock.Verify(db => db.GetAllFireTonesAsync(), Times.AtLeast(2));
                return;
            }
            catch (MockException)
            {
                await Task.Delay(20);
            }
        }

        _dbMock.Verify(db => db.GetAllFireTonesAsync(), Times.AtLeast(2));
    }
}
