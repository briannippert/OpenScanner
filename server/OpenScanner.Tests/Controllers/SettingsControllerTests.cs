using Microsoft.AspNetCore.Mvc;
using Moq;
using OpenScanner.Server.Controllers;
using OpenScanner.Server.Interfaces;
using Xunit;

namespace OpenScanner.Tests.Controllers;

public class SettingsControllerTests
{
    private readonly Mock<IDatabase> _dbMock;
    private readonly Mock<ITranscriptionService> _transcriptionMock;
    private readonly SettingsController _controller;

    public SettingsControllerTests()
    {
        _dbMock = new Mock<IDatabase>();
        _transcriptionMock = new Mock<ITranscriptionService>();
        _controller = new SettingsController(_dbMock.Object, _transcriptionMock.Object);
    }

    [Fact]
    public async Task GetSettings_ReturnsDictionary()
    {
        // Arrange
        var settings = new Dictionary<string, string> { { "Key", "Value" } };
        _dbMock.Setup(db => db.GetAllSettingsAsync()).ReturnsAsync(settings);

        // Act
        var result = await _controller.GetSettings();

        // Assert
        Assert.Equal("Value", result["Key"]);
    }

    [Fact]
    public async Task UpdateSetting_UpdatesKey()
    {
        // Act
        var result = await _controller.UpdateSetting("Theme", "Dark") as OkResult;

        // Assert
        Assert.NotNull(result);
        _dbMock.Verify(db => db.SetSettingAsync("Theme", "Dark"), Times.Once);
        _transcriptionMock.Verify(t => t.PrepareModel(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateSetting_TranscriptionModel_TriggersModelDownload()
    {
        // Act
        var result = await _controller.UpdateSetting("TranscriptionModel", "medium.en") as OkResult;

        // Assert
        Assert.NotNull(result);
        _dbMock.Verify(db => db.SetSettingAsync("TranscriptionModel", "medium.en"), Times.Once);
        _transcriptionMock.Verify(t => t.PrepareModel("medium.en"), Times.Once);
    }
}
