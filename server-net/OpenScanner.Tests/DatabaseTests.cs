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
        var log = new CallLog("test_id", DateTime.UtcNow.ToString("o"), 155.0, "Tag", "Desc", 45.0, -122.0, "audio.raw", 5.0, "Test transcription", 1234, 5678);
        _db.SaveTransmission(log);

        var history = _db.GetHistory(1);
        Assert.Single(history);
        var entry = history.First();
        Assert.Equal("Test transcription", entry.Transcription);
        Assert.Equal(1234, entry.SourceID);
        Assert.Equal(5678, entry.TargetID);
    }

    [Fact]
    public void HierarchicalNavigation_ShouldReturnCorrectNodes()
    {
        // Setup: Add logs across different dates
        // Note: SQLite defaults to UTC if not specified, but the code uses string dates. 
        // We need to ensure the format matches what SQLite expects for strftime, which is typically ISO8601 (YYYY-MM-DD...)
        
        var date1 = "2023-01-15T10:00:00";
        var date2 = "2023-01-15T11:00:00";
        var date3 = "2023-02-01T10:00:00";
        var date4 = "2024-01-01T10:00:00";

        _db.SaveTransmission(new CallLog("1", date1, 155.0, "ChanA", "Desc1", null, null, null, 1));
        _db.SaveTransmission(new CallLog("2", date2, 155.0, "ChanA", "Desc2", null, null, null, 1));
        _db.SaveTransmission(new CallLog("3", date3, 156.0, "ChanB", "Desc3", null, null, null, 1));
        _db.SaveTransmission(new CallLog("4", date4, 157.0, "ChanC", "Desc4", null, null, null, 1));

        // Test Years
        var years = _db.GetTransmissionYears().ToList();
        Assert.Contains("2023", years);
        Assert.Contains("2024", years);
        Assert.Equal(2, years.Count);

        // Test Months for 2023
        var months23 = _db.GetTransmissionMonths("2023").ToList();
        Assert.Contains("01", months23);
        Assert.Contains("02", months23);
        Assert.Equal(2, months23.Count);

        // Test Days for 2023-01
        var days23_01 = _db.GetTransmissionDays("2023", "01").ToList();
        Assert.Single(days23_01);
        Assert.Equal("15", days23_01[0]);

        // Test Channels for 2023-01-15
        var channels = _db.GetTransmissionChannels("2023", "01", "15").ToList();
        Assert.Single(channels);
        Assert.Equal("ChanA", channels[0].alphaTag); // Dynamic access

        // Test Filtered Transmissions
        var logs = _db.GetTransmissions("2023", "01", "15", "ChanA", 155.0).ToList();
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public void SearchTransmissions_ShouldFindMatches()
    {
        _db.SaveTransmission(new CallLog("1", "2024-01-01", 155.0, "Police Dispatch", "Emergency call", null, null, null, 1, "Suspect fleeing north"));
        _db.SaveTransmission(new CallLog("2", "2024-01-01", 156.0, "Fire Dispatch", "Building fire", null, null, null, 1, "Deploying ladder"));
        _db.SaveTransmission(new CallLog("3", "2024-01-01", 157.0, "Taxi", "Ride request", null, null, null, 1, "Pickup at station"));

        // Search by AlphaTag
        var police = _db.SearchTransmissions("Police").ToList();
        Assert.Single(police);
        Assert.Equal("1", police[0].Id);

        // Search by Description
        var fire = _db.SearchTransmissions("Building").ToList();
        Assert.Single(fire);
        Assert.Equal("2", fire[0].Id);

        // Search by Transcription
        var suspect = _db.SearchTransmissions("fleeing").ToList();
        Assert.Single(suspect);
        Assert.Equal("1", suspect[0].Id);

        // Search by Frequency string
        var freq = _db.SearchTransmissions("157").ToList();
        Assert.Single(freq);
        Assert.Equal("3", freq[0].Id);
    }

    public void Dispose()
    {
        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }
    }
}
