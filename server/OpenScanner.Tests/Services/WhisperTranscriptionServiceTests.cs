using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests.Services;

public class WhisperTranscriptionServiceTests
{
    private readonly Mock<IDatabase> _dbMock = new();
    private readonly Mock<ILogger<WhisperTranscriptionService>> _loggerMock = new();
    private readonly Mock<IConfiguration> _configMock = new();

    [Fact]
    public async Task QueueTranscription_ProcessesJobAndFiresCompletedEvent()
    {
        // Arrange
        _dbMock.Setup(db => db.GetSettingAsync("TranscriptionThreads")).ReturnsAsync("2");
        _dbMock.Setup(db => db.GetSettingAsync("EnableTranscription")).ReturnsAsync("false");

        using var service = new WhisperTranscriptionService(_dbMock.Object, _loggerMock.Object, _configMock.Object);

        var log = new CallLog
        {
            Id = "test_log_id",
            Frequency = 155.0
        };

        var tcs = new TaskCompletionSource<CallLog>();
        service.OnTranscriptionCompleted += (completedLog) =>
        {
            tcs.SetResult(completedLog);
        };

        // Act
        service.QueueTranscription(log, "dummy_path.wav");

        // Assert
        var resultLog = await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task 
            ? await tcs.Task 
            : null;

        Assert.NotNull(resultLog);
        Assert.Equal("test_log_id", resultLog.Id);
        Assert.Null(resultLog.Transcription); // Null because EnableTranscription is false
        
        _dbMock.Verify(db => db.UpdateTranscriptionAsync("test_log_id", null), Times.Once);
    }
}
