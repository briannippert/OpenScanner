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

    [Fact]
    public void BuildWhisperArgs_IncludesAccuracyFlagsAndModelPromptWav()
    {
        var args = WhisperTranscriptionService.BuildWhisperArgs(
            "/models/ggml-large-v3-turbo-q5_0.bin", "/tmp/clip.16k.wav", "radio prompt", 5, 4, null);

        Assert.Contains("-m \"/models/ggml-large-v3-turbo-q5_0.bin\"", args);
        Assert.Contains("-f \"/tmp/clip.16k.wav\"", args);
        Assert.Contains("--prompt \"radio prompt\"", args);
        Assert.Contains("-l en", args);
        Assert.Contains("-nt", args);
        // Accuracy / anti-hallucination flags.
        Assert.Contains("-bs 5", args);
        Assert.Contains("-bo 5", args);
        Assert.Contains("-t 4", args);
        Assert.Contains("-mc 0", args);
        Assert.Contains("-et 2.8", args);
    }

    [Fact]
    public void BuildWhisperArgs_AppendsExtraArgsWhenProvided()
    {
        var args = WhisperTranscriptionService.BuildWhisperArgs(
            "/m.bin", "/a.wav", "p", 5, 4, "  --vad  ");

        Assert.EndsWith("--vad", args);
    }

    [Fact]
    public void BuildWhisperArgs_OmitsExtraArgsWhenNullOrBlank()
    {
        var args = WhisperTranscriptionService.BuildWhisperArgs("/m.bin", "/a.wav", "p", 5, 4, "   ");
        // Blank ExtraArgs must not append anything after the prompt.
        Assert.EndsWith("--prompt \"p\"", args);
    }
}
