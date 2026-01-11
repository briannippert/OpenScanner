using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using OpenScanner.Server.Controllers;
using OpenScanner.Server.Interfaces;
using Xunit;

namespace OpenScanner.Tests.Controllers;

public class SupportControllerTests
{
    private readonly Mock<ISupportService> _supportMock;
    private readonly Mock<ILogger<SupportController>> _loggerMock;
    private readonly SupportController _controller;

    public SupportControllerTests()
    {
        _supportMock = new Mock<ISupportService>();
        _loggerMock = new Mock<ILogger<SupportController>>();
        _controller = new SupportController(_supportMock.Object, _loggerMock.Object);
    }

    [Fact]
    public void GetSystemInfo_ReturnsVersionInfo()
    {
        // Arrange
        var info = new Dictionary<string, string> { { "Commit", "abc" } };
        _supportMock.Setup(s => s.GetVersionInfo()).Returns(info);

        // Act
        var result = _controller.GetSystemInfo() as OkObjectResult;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(info, result.Value);
    }

    [Fact]
    public async Task GetSupportPackage_ReturnsZipFile()
    {
        // Arrange
        var data = new byte[] { 0x1, 0x2, 0x3 };
        _supportMock.Setup(s => s.CreateSupportPackageAsync()).ReturnsAsync(data);

        // Act
        var result = await _controller.GetSupportPackage() as FileContentResult;

        // Assert
        Assert.NotNull(result);
        Assert.Equal("application/zip", result.ContentType);
        Assert.Equal(data, result.FileContents);
        Assert.Contains("openscanner_support_", result.FileDownloadName);
    }

    [Fact]
    public async Task GetSupportPackage_LogErrorAndReturn500_OnException()
    {
        // Arrange
        _supportMock.Setup(s => s.CreateSupportPackageAsync()).ThrowsAsync(new Exception("Test error"));

        // Act
        var result = await _controller.GetSupportPackage() as ObjectResult;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(500, result.StatusCode);
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
            Times.Once);
    }
}