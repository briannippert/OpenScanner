using OpenScanner.Server;
using OpenScanner.Server.Models;
using Xunit;

namespace OpenScanner.Tests;

public class DatabaseTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly Database _db;

    public DatabaseTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"openscanner_test_{Guid.NewGuid()}.db");
        _db = new Database($"Data Source={_testDbPath}");
    }

    [Fact]
    public void AddChannel_ShouldReturnIdAndBePersisted()
    {
        var channel = new Channel(162.400, "NOAA Weather", "Weather radio");
        var id = _db.AddChannel(channel);

        Assert.True(id > 0);
        var channels = _db.GetAllChannels();
        Assert.Contains(channels, c => c.Frequency == 162.400 && c.AlphaTag == "NOAA Weather");
    }

    [Fact]
    public void UpdateChannel_ShouldReflectChanges()
    {
        var channel = new Channel(155.000, "Old Tag", "Old Desc");
        var id = _db.AddChannel(channel);
        var updated = new Channel(155.000, "New Tag", "New Desc") { Id = id };

        _db.UpdateChannel(updated);

        var channels = _db.GetAllChannels();
        var c = channels.First(x => x.Id == id);
        Assert.Equal("New Tag", c.AlphaTag);
        Assert.Equal("New Desc", c.Description);
    }

    [Fact]
    public void SaveTransmission_ShouldPersistLog()
    {
        var log = new CallLog("test_id", DateTime.UtcNow.ToString("o"), 155.0, "Tag", "Desc", 45.0, -122.0, "audio.raw", 5.0, "Test transcription");
        _db.SaveTransmission(log);

        var history = _db.GetHistory(1);
        Assert.Single(history);
        Assert.Equal("Test transcription", history.First().Transcription);
    }

    public void Dispose()
    {
        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }
    }
}
