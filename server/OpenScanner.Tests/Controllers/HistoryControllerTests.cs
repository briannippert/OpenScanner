using Microsoft.AspNetCore.Mvc;
using Moq;
using OpenScanner.Server.Controllers;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using Xunit;

namespace OpenScanner.Tests.Controllers;

public class HistoryControllerTests
{
    private readonly Mock<IDatabase> _dbMock;
    private readonly HistoryController _controller;

    public HistoryControllerTests()
    {
        _dbMock = new Mock<IDatabase>();
        _controller = new HistoryController(_dbMock.Object);
    }

    [Theory]
    [InlineData(28, 28)]
    [InlineData(7, 7)]
    // Out-of-range windows are clamped rather than rejected.
    [InlineData(0, 1)]
    [InlineData(5000, 90)]
    public async Task GetActivity_QueriesTheRequestedWindow(int requestedDays, int expectedDays)
    {
        DateTime? since = null;
        _dbMock.Setup(db => db.GetTransmissionHourlyCountsAsync(It.IsAny<DateTime>()))
            .Callback<DateTime>(d => since = d)
            .ReturnsAsync(new List<ActivityBucket> { new() { Hour = "2026-03-10T14:00:00Z", Count = 2 } });

        var result = await _controller.GetActivity(requestedDays);

        Assert.Single(result);
        Assert.NotNull(since);
        var windowDays = (DateTime.UtcNow - since!.Value).TotalDays;
        Assert.InRange(windowDays, expectedDays - 0.01, expectedDays + 0.01);
    }

    [Fact]
    public async Task GetHistory_ReturnsLogs()
    {
        // Arrange
        var logs = new List<CallLog> { new CallLog { Id = "1" } };
        _dbMock.Setup(db => db.GetHistoryAsync(100)).ReturnsAsync(logs);

        // Act
        var result = await _controller.GetHistory();

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public async Task GetById_ReturnsLog_WhenFound()
    {
        // Arrange
        var log = new CallLog { Id = "log_123" };
        _dbMock.Setup(db => db.GetTransmissionByIdAsync("log_123")).ReturnsAsync(log);

        // Act
        var result = await _controller.GetById("log_123");

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(log, ok.Value);
    }

    [Fact]
    public async Task GetById_Returns404_WhenMissing()
    {
        // Arrange
        _dbMock.Setup(db => db.GetTransmissionByIdAsync("nope")).ReturnsAsync((CallLog?)null);

        // Act
        var result = await _controller.GetById("nope");

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetYears_ReturnsYears()
    {
        // Arrange
        var years = new List<string> { "2023", "2024" };
        _dbMock.Setup(db => db.GetTransmissionYearsAsync()).ReturnsAsync(years);

        // Act
        var result = await _controller.GetYears();

        // Assert
        Assert.Equal(2, result.Count());
    }

    [Fact]
    public async Task GetMonths_ReturnsMonths()
    {
        // Arrange
        var months = new List<string> { "01", "02" };
        _dbMock.Setup(db => db.GetTransmissionMonthsAsync("2024")).ReturnsAsync(months);

        // Act
        var result = await _controller.GetMonths("2024");

        // Assert
        Assert.Equal(2, result.Count());
    }

    [Fact]
    public async Task GetDays_ReturnsDays()
    {
        // Arrange
        var days = new List<string> { "01", "15" };
        _dbMock.Setup(db => db.GetTransmissionDaysAsync("2024", "01")).ReturnsAsync(days);

        // Act
        var result = await _controller.GetDays("2024", "01");

        // Assert
        Assert.Equal(2, result.Count());
    }

    [Fact]
    public async Task GetChannelsForDay_ReturnsChannelData()
    {
        // Arrange
        var data = new List<dynamic> { new { Name = "Police", Count = 5 } };
        _dbMock.Setup(db => db.GetTransmissionChannelsAsync("2024", "01", "15")).ReturnsAsync(data);

        // Act
        var result = await _controller.GetChannelsForDay("2024", "01", "15");

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public async Task GetFilteredTransmissions_ReturnsLogs()
    {
        // Arrange
        var logs = new List<CallLog> { new CallLog { Id = "1" } };
        _dbMock.Setup(db => db.GetTransmissionsAsync("2024", "01", "15", "Police", 155.0)).ReturnsAsync(logs);

        // Act
        var result = await _controller.GetFilteredTransmissions("2024", "01", "15", "Police", 155.0);

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public async Task SearchTransmissions_ReturnsLogs()
    {
        // Arrange
        var logs = new List<CallLog> { new CallLog { Id = "1" } };
        _dbMock.Setup(db => db.SearchTransmissionsAsync("fire")).ReturnsAsync(logs);

        // Act
        var result = await _controller.SearchTransmissions("fire");

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public async Task DeleteTransmission_DeletesLog()
    {
        // Act
        var result = await _controller.DeleteTransmission("log_123") as OkResult;

        // Assert
        Assert.NotNull(result);
        _dbMock.Verify(db => db.DeleteTransmissionAsync("log_123"), Times.Once);
    }

    [Fact]
    public async Task ClearHistory_ClearsAll()
    {
        // Act
        var result = await _controller.ClearHistory() as OkResult;

        // Assert
        Assert.NotNull(result);
        _dbMock.Verify(db => db.ClearHistoryAsync(), Times.Once);
    }
}
