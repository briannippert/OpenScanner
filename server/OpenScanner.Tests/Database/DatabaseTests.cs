using OpenScanner.Server;
using OpenScanner.Server.Models;
using OpenScanner.Server.Interfaces;
using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace OpenScanner.Tests;

public class DatabaseTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly Database _db;

    public DatabaseTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"openscanner_test_{Guid.NewGuid()}.db");
        
        var inMemorySettings = new Dictionary<string, string?> {
            {"ConnectionStrings:DefaultConnection", $"Data Source={_testDbPath}"}
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var loggerMock = new Mock<ILogger<Database>>();

        _db = new Database(configuration, loggerMock.Object);
    }

    [Fact]
    public async Task AddChannel_ShouldReturnIdAndBePersisted()
    {
        var channel = new Channel(162.400, "NOAA Weather", "Weather radio");
        var id = await _db.AddChannelAsync(channel);

        Assert.True(id > 0);
        var channels = await _db.GetAllChannelsAsync();
        Assert.Contains(channels, c => c.Frequency == 162.400 && c.AlphaTag == "NOAA Weather");
    }

    [Fact]
    public async Task UpdateChannel_ShouldReflectChanges()
    {
        var channel = new Channel(155.000, "Old Tag", "Old Desc");
        var id = await _db.AddChannelAsync(channel);
        var updated = new Channel(155.000, "New Tag", "New Desc") { Id = id };

        await _db.UpdateChannelAsync(updated);

        var channels = await _db.GetAllChannelsAsync();
        var c = channels.First(x => x.Id == id);
        Assert.Equal("New Tag", c.AlphaTag);
        Assert.Equal("New Desc", c.Description);
    }

    [Fact]
    public async Task SaveTransmission_ShouldPersistLog()
    {
        var log = new CallLog("test_id", DateTime.UtcNow.ToString("o"), 155.0, "Tag", "Desc", 45.0, -122.0, "audio.raw", 5.0, "Test transcription", 1234, 5678);
        await _db.SaveTransmissionAsync(log);

        var history = await _db.GetHistoryAsync(1);
        Assert.Single(history);
        var entry = history.First();
        Assert.Equal("Test transcription", entry.Transcription);
        Assert.Equal(1234, entry.SourceID);
        Assert.Equal(5678, entry.TargetID);
    }

    [Fact]
    public async Task HierarchicalNavigation_ShouldReturnCorrectNodes()
    {
        var date1 = "2023-01-15T10:00:00";
        var date2 = "2023-01-15T11:00:00";
        var date3 = "2023-02-01T10:00:00";
        var date4 = "2024-01-01T10:00:00";

        await _db.SaveTransmissionAsync(new CallLog("1", date1, 155.0, "ChanA", "Desc1", null, null, null, 1));
        await _db.SaveTransmissionAsync(new CallLog("2", date2, 155.0, "ChanA", "Desc2", null, null, null, 1));
        await _db.SaveTransmissionAsync(new CallLog("3", date3, 156.0, "ChanB", "Desc3", null, null, null, 1));
        await _db.SaveTransmissionAsync(new CallLog("4", date4, 157.0, "ChanC", "Desc4", null, null, null, 1));

        // Test Years
        var years = (await _db.GetTransmissionYearsAsync()).ToList();
        Assert.Contains("2023", years);
        Assert.Contains("2024", years);
        Assert.Equal(2, years.Count);

        // Test Months for 2023
        var months23 = (await _db.GetTransmissionMonthsAsync("2023")).ToList();
        Assert.Contains("01", months23);
        Assert.Contains("02", months23);
        Assert.Equal(2, months23.Count);

        // Test Days for 2023-01
        var days23_01 = (await _db.GetTransmissionDaysAsync("2023", "01")).ToList();
        Assert.Single(days23_01);
        Assert.Equal("15", days23_01[0]);

        // Test Channels for 2023-01-15
        var channels = (await _db.GetTransmissionChannelsAsync("2023", "01", "15")).ToList();
        Assert.Single(channels);
        Assert.Equal("ChanA", channels[0].alphaTag);

        // Test Filtered Transmissions
        var logs = (await _db.GetTransmissionsAsync("2023", "01", "15", "ChanA", 155.0)).ToList();
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public async Task SearchTransmissions_ShouldFindMatches()
    {
        await _db.SaveTransmissionAsync(new CallLog("1", "2024-01-01", 155.0, "Police Dispatch", "Emergency call", null, null, null, 1, "Suspect fleeing north"));
        await _db.SaveTransmissionAsync(new CallLog("2", "2024-01-01", 156.0, "Fire Dispatch", "Building fire", null, null, null, 1, "Deploying ladder"));
        await _db.SaveTransmissionAsync(new CallLog("3", "2024-01-01", 157.0, "Taxi", "Ride request", null, null, null, 1, "Pickup at station"));

        // Search by AlphaTag
        var police = (await _db.SearchTransmissionsAsync("Police")).ToList();
        Assert.Single(police);
        Assert.Equal("1", police[0].Id);

        // Search by Description
        var fire = (await _db.SearchTransmissionsAsync("Building")).ToList();
        Assert.Single(fire);
        Assert.Equal("2", fire[0].Id);

        // Search by Transcription
        var suspect = (await _db.SearchTransmissionsAsync("fleeing")).ToList();
        Assert.Single(suspect);
        Assert.Equal("1", suspect[0].Id);

        // Search by Frequency string
        var freq = (await _db.SearchTransmissionsAsync("157")).ToList();
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