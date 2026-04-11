using Microsoft.Extensions.Logging;
using Moq;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests;

public class TranscriptionServiceRouterTests
{
    private readonly Mock<IDatabase> _dbMock = new();
    private readonly Mock<WhisperTranscriptionService> _localMock;
    private readonly Mock<RemoteWhisperTranscriptionService> _remoteMock;
    private readonly Mock<ILogger<TranscriptionServiceRouter>> _loggerMock = new();

    public TranscriptionServiceRouterTests()
    {
        var localLogger = new Mock<ILogger<WhisperTranscriptionService>>();
        var remoteLogger = new Mock<ILogger<RemoteWhisperTranscriptionService>>();
        var config = new Mock<Microsoft.Extensions.Configuration.IConfiguration>();

        _localMock = new Mock<WhisperTranscriptionService>(
            _dbMock.Object, localLogger.Object, config.Object);

        _remoteMock = new Mock<RemoteWhisperTranscriptionService>(
            _dbMock.Object, remoteLogger.Object, new HttpClient());
    }

    [Fact]
    public void TranscribeAudio_DefaultsToLocal_WhenModeNotSet()
    {
        _dbMock.Setup(d => d.GetSettingAsync("TranscriptionMode"))
            .ReturnsAsync((string?)null);
        _localMock.Setup(s => s.TranscribeAudio(It.IsAny<string>()))
            .Returns("local result");

        var router = new TranscriptionServiceRouter(
            _localMock.Object, _remoteMock.Object, _dbMock.Object, _loggerMock.Object);

        var result = router.TranscribeAudio("/tmp/test.wav");

        Assert.Equal("local result", result);
        _localMock.Verify(s => s.TranscribeAudio("/tmp/test.wav"), Times.Once);
        _remoteMock.Verify(s => s.TranscribeAudio(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void TranscribeAudio_UsesLocal_WhenModeIsLocal()
    {
        _dbMock.Setup(d => d.GetSettingAsync("TranscriptionMode"))
            .ReturnsAsync("local");
        _localMock.Setup(s => s.TranscribeAudio(It.IsAny<string>()))
            .Returns("local result");

        var router = new TranscriptionServiceRouter(
            _localMock.Object, _remoteMock.Object, _dbMock.Object, _loggerMock.Object);

        var result = router.TranscribeAudio("/tmp/test.wav");

        Assert.Equal("local result", result);
        _localMock.Verify(s => s.TranscribeAudio("/tmp/test.wav"), Times.Once);
        _remoteMock.Verify(s => s.TranscribeAudio(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void TranscribeAudio_UsesRemote_WhenModeIsRemote()
    {
        _dbMock.Setup(d => d.GetSettingAsync("TranscriptionMode"))
            .ReturnsAsync("remote");
        _remoteMock.Setup(s => s.TranscribeAudio(It.IsAny<string>()))
            .Returns("remote result");

        var router = new TranscriptionServiceRouter(
            _localMock.Object, _remoteMock.Object, _dbMock.Object, _loggerMock.Object);

        var result = router.TranscribeAudio("/tmp/test.wav");

        Assert.Equal("remote result", result);
        _remoteMock.Verify(s => s.TranscribeAudio("/tmp/test.wav"), Times.Once);
        _localMock.Verify(s => s.TranscribeAudio(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void TranscribeAudio_UsesRemote_CaseInsensitive()
    {
        _dbMock.Setup(d => d.GetSettingAsync("TranscriptionMode"))
            .ReturnsAsync("Remote");
        _remoteMock.Setup(s => s.TranscribeAudio(It.IsAny<string>()))
            .Returns("remote result");

        var router = new TranscriptionServiceRouter(
            _localMock.Object, _remoteMock.Object, _dbMock.Object, _loggerMock.Object);

        var result = router.TranscribeAudio("/tmp/test.wav");

        Assert.Equal("remote result", result);
        _remoteMock.Verify(s => s.TranscribeAudio("/tmp/test.wav"), Times.Once);
    }
}
