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
