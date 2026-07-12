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
    public async Task FreshInstall_ShouldHaveNoDefaultChannels()
    {
        var channels = await _db.GetAllChannelsAsync();
        Assert.Empty(channels);
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
    public async Task AddAlias_ShouldPersist_AndUpsertOnConflict()
    {
        var id = await _db.AddAliasAsync(new RadioAlias { Kind = "SRC", Value = 101, Name = "Car 1", AlphaTag = "PD", Frequency = 155.0 });
        Assert.True(id > 0);

        var all = await _db.GetAliasesAsync();
        Assert.Single(all);
        Assert.Equal("Car 1", all.First().Name);

        // Same (kind, value, alphaTag, frequency) upserts the name rather than adding a row.
        await _db.AddAliasAsync(new RadioAlias { Kind = "SRC", Value = 101, Name = "Engine 1", AlphaTag = "PD", Frequency = 155.0 });
        all = await _db.GetAliasesAsync();
        Assert.Single(all);
        Assert.Equal("Engine 1", all.First().Name);
    }

    [Fact]
    public async Task UpdateAndDeleteAlias_ShouldWork()
    {
        var id = await _db.AddAliasAsync(new RadioAlias { Kind = "TG", Value = 4021, Name = "Dispatch", AlphaTag = "FD", Frequency = 154.4 });

        await _db.UpdateAliasAsync(new RadioAlias { Id = id, Kind = "TG", Value = 4021, Name = "Main Dispatch", AlphaTag = "FD", Frequency = 154.4 });
        Assert.Equal("Main Dispatch", (await _db.GetAliasesAsync()).First().Name);

        await _db.DeleteAliasAsync(id);
        Assert.Empty(await _db.GetAliasesAsync());
    }

    [Fact]
    public async Task ImportAliases_ShouldFillBlanks_WithoutOverwriting()
    {
        await _db.AddAliasAsync(new RadioAlias { Kind = "SRC", Value = 101, Name = "Original", AlphaTag = "PD", Frequency = 155.0 });

        var added = await _db.ImportAliasesAsync(new[]
        {
            new RadioAlias { Kind = "SRC", Value = 101, Name = "Should Not Win", AlphaTag = "PD", Frequency = 155.0 }, // conflict → skipped
            new RadioAlias { Kind = "TG", Value = 4021, Name = "Dispatch", AlphaTag = "PD", Frequency = 155.0 },        // new → added
        });

        Assert.Equal(1, added);
        var all = (await _db.GetAliasesAsync()).ToList();
        Assert.Equal(2, all.Count);
        Assert.Equal("Original", all.First(a => a.Kind == "SRC" && a.Value == 101).Name); // not overwritten
        Assert.Contains(all, a => a.Kind == "TG" && a.Value == 4021 && a.Name == "Dispatch");
    }

    [Fact]
    public async Task GetAliasCandidates_ShouldGroupDistinctSrcAndTgPerChannel_WithinWindow()
    {
        var now = DateTime.UtcNow.ToString("o");
        // Same channel + SRC 101 twice (count 2), SRC 202 once, TG 4021.
        await _db.SaveTransmissionAsync(new CallLog("c1", now, 155.0, "PD", "d", null, null, null, 1.0, null, 101, 4021));
        await _db.SaveTransmissionAsync(new CallLog("c2", now, 155.0, "PD", "d", null, null, null, 1.0, null, 101, 4021));
        await _db.SaveTransmissionAsync(new CallLog("c3", now, 155.0, "PD", "d", null, null, null, 1.0, null, 202, null));
        // Stale (30 days ago) must be excluded from the 7-day window.
        var old = DateTime.UtcNow.AddDays(-30).ToString("o");
        await _db.SaveTransmissionAsync(new CallLog("c4", old, 155.0, "PD", "d", null, null, null, 1.0, null, 999, null));

        var cands = (await _db.GetAliasCandidatesAsync(7)).ToList();

        var src = cands.Where(c => c.Kind == "SRC").ToList();
        Assert.Contains(src, c => c.Value == 101 && c.Count == 2 && c.AlphaTag == "PD");
        Assert.Contains(src, c => c.Value == 202 && c.Count == 1);
        Assert.DoesNotContain(src, c => c.Value == 999);
        Assert.Contains(cands, c => c.Kind == "TG" && c.Value == 4021);
    }

    [Fact]
    public async Task GetTransmissionById_ShouldReturnMatchingLog()
    {
        var log = new CallLog("target_id", DateTime.UtcNow.ToString("o"), 155.0, "Tag", "Desc", 45.0, -122.0, "audio.raw", 5.0, "Linked transcription", 1234, 5678);
        await _db.SaveTransmissionAsync(log);

        var found = await _db.GetTransmissionByIdAsync("target_id");
        Assert.NotNull(found);
        Assert.Equal("target_id", found!.Id);
        Assert.Equal("Linked transcription", found.Transcription);

        var missing = await _db.GetTransmissionByIdAsync("does_not_exist");
        Assert.Null(missing);
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

    [Fact]
    public async Task DeleteTransmission_ShouldRemoveFromDatabase()
    {
        var log = new CallLog("delete_test_id", DateTime.UtcNow.ToString("o"), 155.0, "Tag", "Desc", null, null, null, 5.0);
        await _db.SaveTransmissionAsync(log);

        var before = await _db.GetHistoryAsync(100);
        Assert.Contains(before, l => l.Id == "delete_test_id");

        await _db.DeleteTransmissionAsync("delete_test_id");

        var after = await _db.GetHistoryAsync(100);
        Assert.DoesNotContain(after, l => l.Id == "delete_test_id");
    }

    [Fact]
    public async Task DeleteTransmission_WithUnknownId_ShouldNotThrow()
    {
        // Deleting a non-existent ID should complete without throwing an exception.
        // If an exception is thrown, this test will fail.
        await _db.DeleteTransmissionAsync("nonexistent_id");

        // Database should remain empty - the no-op delete did not affect any records.
        var history = await _db.GetHistoryAsync(100);
        Assert.Empty(history);
    }

    public void Dispose()
    {
        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }
    }
}