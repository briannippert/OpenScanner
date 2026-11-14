using OpenScanner.Server;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
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
    public async Task ConcurrentReadsAndWrites_ShouldNotThrowDatabaseLocked()
    {
        // Regression guard for "SQLite Error 5: 'database is locked'": with WAL
        // journaling plus a busy_timeout, many concurrent writers and readers on
        // separate connections must all succeed rather than failing on lock
        // contention (as they did in the default rollback-journal mode).
        var now = DateTime.UtcNow.ToString("o");
        var tasks = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            var id = $"concurrent_{i}";
            tasks.Add(_db.SaveTransmissionAsync(
                new CallLog(id, now, 155.0, "PD", "d", null, null, null, 1.0, null, i, 4021)));
            tasks.Add(_db.AddAliasAsync(
                new RadioAlias { Kind = "SRC", Value = i, Name = $"Unit {i}", AlphaTag = "PD", Frequency = 155.0 }));
            tasks.Add(_db.GetHistoryAsync(100));
        }

        // Any lock contention would surface as a faulted task here.
        await Task.WhenAll(tasks);

        Assert.Equal(50, (await _db.GetHistoryAsync(1000)).Count());
        Assert.Equal(50, (await _db.GetAliasesAsync()).Count());
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
    public async Task GetTransmissionHourlyCounts_ShouldBucketByUtcHour_WithinWindow()
    {
        CallLog At(string id, string timestamp) => new(id, timestamp, 155.0, "PD", "d", null, null, null, 1.0, null, null, null);
        await _db.SaveTransmissionAsync(At("h1", "2026-03-10T14:05:00.0000000Z"));
        await _db.SaveTransmissionAsync(At("h2", "2026-03-10T14:59:59.9990000Z"));
        await _db.SaveTransmissionAsync(At("h3", "2026-03-10T15:00:00.0000000Z"));
        await _db.SaveTransmissionAsync(At("h4", "2026-03-12T03:30:00.0000000Z"));
        // Before the window: must not be counted.
        await _db.SaveTransmissionAsync(At("h5", "2026-03-09T23:59:59.0000000Z"));

        var since = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        var buckets = (await _db.GetTransmissionHourlyCountsAsync(since)).ToList();

        Assert.Equal(
            new[] { ("2026-03-10T14:00:00Z", 2), ("2026-03-10T15:00:00Z", 1), ("2026-03-12T03:00:00Z", 1) },
            buckets.Select(b => (b.Hour, b.Count)));
    }

    [Fact]
    public async Task GetTransmissionStats_ShouldAggregateTheWindow()
    {
        const string inWindow = "2026-03-10T14:05:00.0000000Z";
        await _db.SaveTransmissionAsync(new CallLog("s1", inWindow, 155.0, "PD", "d", null, null, null, 10.0, "unit 4 responding", 101, 4021));
        await _db.SaveTransmissionAsync(new CallLog("s2", inWindow, 155.0, "PD", "d", null, null, null, 20.0, null, 101, 4021));
        await _db.SaveTransmissionAsync(new CallLog("s3", inWindow, 155.0, "PD", "d", null, null, null, 5.0, "", 202, 4021));
        await _db.SaveTransmissionAsync(new CallLog("s4", inWindow, 154.43, "Fire", "d", null, null, null, 30.0, "station 4", null, null) { DetectedTone = "Station 4" });
        // Before the window: must not appear anywhere.
        await _db.SaveTransmissionAsync(new CallLog("s5", "2026-03-01T00:00:00.0000000Z", 460.1, "Old", "d", null, null, null, 99.0, "old", 999, 999) { DetectedTone = "Old Tone" });

        var stats = await _db.GetTransmissionStatsAsync(new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), top: 10);

        Assert.Equal(4, stats.Summary.Transmissions);
        Assert.Equal(65.0, stats.Summary.AirtimeSeconds);
        Assert.Equal(30.0, stats.Summary.LongestSeconds);
        Assert.Equal(2, stats.Summary.Transcribed);   // an empty transcript does not count
        Assert.Equal(1, stats.Summary.ToneOuts);
        Assert.Equal(2, stats.Summary.ActiveChannels);

        Assert.Equal(new[] { ("PD", 3, 35.0), ("Fire", 1, 30.0) }, stats.Channels.Select(c => (c.AlphaTag, c.Count, c.AirtimeSeconds)));
        Assert.Equal(new[] { (4021L, 3) }, stats.Talkgroups.Select(t => (t.Id, t.Count)));
        Assert.Equal(new[] { (101L, 2), (202L, 1) }, stats.Sources.Select(s => (s.Id, s.Count)));
        Assert.Equal(new[] { ("Station 4", 1) }, stats.Tones.Select(t => (t.Name, t.Count)));
    }

    [Fact]
    public async Task GetTransmissionStats_OnAnEmptyWindow_ReturnsZeros()
    {
        var stats = await _db.GetTransmissionStatsAsync(DateTime.UtcNow.AddDays(-7), top: 10);

        Assert.Equal(0, stats.Summary.Transmissions);
        Assert.Equal(0, stats.Summary.AirtimeSeconds);
        Assert.Empty(stats.Channels);
        Assert.Empty(stats.Tones);
    }

    [Fact]
    public async Task GetTransmissionStats_ShouldHonourTheTopLimit()
    {
        var now = DateTime.UtcNow.ToString("o");
        for (var i = 0; i < 5; i++)
            await _db.SaveTransmissionAsync(new CallLog($"t{i}", now, 150.0 + i, $"Ch{i}", "d", null, null, null, 1.0, null, null, null));

        var stats = await _db.GetTransmissionStatsAsync(DateTime.UtcNow.AddDays(-1), top: 3);

        Assert.Equal(3, stats.Channels.Count);
        Assert.Equal(5, stats.Summary.ActiveChannels);
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

    private static CallLog Tx(string id, DateTime when, string? transcript = "Engine 2 respond") =>
        new(id, when.ToString("o"), 154.0, "FD Dispatch", "", null, null, $"{id}.raw", 3, transcript);

    private static TransmissionInsight Incident(string id, string callType, string service, double? lat = 42.78, double? lon = -71.23) => new()
    {
        TransmissionId = id, AnalyzedAt = DateTime.UtcNow.ToString("o"), IsIncident = true,
        CallType = callType, Service = service, Summary = "s", Address = "45 Main Street", Lat = lat, Lon = lon,
    };

    [Fact]
    public async Task Insight_SaveAndGet_RoundTrips()
    {
        await _db.SaveTransmissionAsync(Tx("a", DateTime.UtcNow));
        await _db.SaveInsightAsync(Incident("a", "fire", "fire"));

        var got = await _db.GetInsightAsync("a");
        Assert.NotNull(got);
        Assert.True(got!.IsIncident);
        Assert.Equal("fire", got.CallType);
        Assert.Equal(42.78, got.Lat);

        // Upsert replaces.
        await _db.SaveInsightAsync(Incident("a", "medical", "fire"));
        Assert.Equal("medical", (await _db.GetInsightAsync("a"))!.CallType);
    }

    [Fact]
    public async Task GetMapCalls_FiltersByWindowTypeServiceAndLocation()
    {
        var now = DateTime.UtcNow;
        await _db.SaveTransmissionAsync(Tx("fire1", now.AddMinutes(-5)));
        await _db.SaveTransmissionAsync(Tx("med1", now.AddMinutes(-10)));
        await _db.SaveTransmissionAsync(Tx("theft1", now.AddMinutes(-15)));
        await _db.SaveTransmissionAsync(Tx("old", now.AddDays(-3)));
        await _db.SaveTransmissionAsync(Tx("nogeo", now.AddMinutes(-1)));
        await _db.SaveTransmissionAsync(Tx("routine", now.AddMinutes(-1)));
        await _db.SaveInsightAsync(Incident("fire1", "fire", "fire"));
        await _db.SaveInsightAsync(Incident("med1", "medical", "fire"));
        await _db.SaveInsightAsync(Incident("theft1", "theft", "police"));
        await _db.SaveInsightAsync(Incident("old", "fire", "fire"));
        await _db.SaveInsightAsync(Incident("nogeo", "fire", "fire", null, null));
        await _db.SaveInsightAsync(new TransmissionInsight { TransmissionId = "routine", AnalyzedAt = now.ToString("o") });

        var since = now.AddDays(-1).ToString("o");
        var all = (await _db.GetMapCallsAsync(new MapCallFilter(since, null, null, null, 100))).ToList();
        Assert.Equal(new[] { "fire1", "med1", "theft1" }, all.Select(c => c.Id));
        Assert.Equal("fire1.raw", all[0].AudioPath);

        var fireOnly = await _db.GetMapCallsAsync(new MapCallFilter(since, null, new[] { "fire", "theft" }, null, 100));
        Assert.Equal(new[] { "fire1", "theft1" }, fireOnly.Select(c => c.Id));

        var police = await _db.GetMapCallsAsync(new MapCallFilter(since, null, null, "police", 100));
        Assert.Equal(new[] { "theft1" }, police.Select(c => c.Id));

        var until = await _db.GetMapCallsAsync(new MapCallFilter(since, now.AddMinutes(-8).ToString("o"), null, null, 100));
        Assert.Equal(new[] { "med1", "theft1" }, until.Select(c => c.Id));
    }

    [Fact]
    public async Task Channel_CityAndState_RoundTripAndLookUpByFrequency()
    {
        var id = await _db.AddChannelAsync(new Channel(154.43, "Salem FD", "Fire") { City = "Salem", State = "NH" });
        var got = await _db.GetChannelByFrequencyAsync(154.43);
        Assert.NotNull(got);
        Assert.Equal("Salem", got!.City);
        Assert.Equal("NH", got.State);

        got.City = "Windham";
        await _db.UpdateChannelAsync(got);
        Assert.Equal("Windham", (await _db.GetAllChannelsAsync()).Single(c => c.Id == id).City);

        Assert.Null(await _db.GetChannelByFrequencyAsync(460.1));
    }

    [Fact]
    public async Task IncidentGrouping_SameServiceNearbyAndRecentShareAnIncident()
    {
        var t0 = DateTime.UtcNow.AddHours(-2);
        async Task<string> Analyze(string id, DateTime when, string service, double lat = 42.78, double lon = -71.23)
        {
            await _db.SaveTransmissionAsync(Tx(id, when));
            var insight = Incident(id, "fire", service, lat, lon);
            insight.IncidentId = await IncidentGrouping.ResolveAsync(_db, id, service, lat, lon, when.ToString("o"));
            await _db.SaveInsightAsync(insight);
            return insight.IncidentId;
        }

        Assert.Equal("dispatch", await Analyze("dispatch", t0, "fire"));
        // 50 m away, 5 min later: same incident.
        Assert.Equal("dispatch", await Analyze("onscene", t0.AddMinutes(5), "fire", 42.78045));
        // Chains: 18 min after dispatch but 13 min after "onscene".
        Assert.Equal("dispatch", await Analyze("cleared", t0.AddMinutes(18), "fire"));
        // Police at the same address stays separate.
        Assert.Equal("pd", await Analyze("pd", t0.AddMinutes(1), "police"));
        // Same service but a different street (~1 km).
        Assert.Equal("elsewhere", await Analyze("elsewhere", t0.AddMinutes(2), "fire", 42.79));
        // Same spot, an hour later: a new incident.
        Assert.Equal("later", await Analyze("later", t0.AddMinutes(80), "fire"));

        var calls = (await _db.GetMapCallsAsync(new MapCallFilter(t0.AddMinutes(-1).ToString("o"), null, null, null, 100))).ToList();
        Assert.Equal("dispatch", calls.Single(c => c.Id == "cleared").IncidentId);
    }

    [Fact]
    public async Task IncidentGrouping_BackfillOrderNewestFirstStillGroups()
    {
        var t0 = DateTime.UtcNow.AddHours(-2);
        await _db.SaveTransmissionAsync(Tx("first", t0));
        await _db.SaveTransmissionAsync(Tx("second", t0.AddMinutes(4)));

        var second = Incident("second", "fire", "fire");
        second.IncidentId = await IncidentGrouping.ResolveAsync(_db, "second", "fire", 42.78, -71.23, t0.AddMinutes(4).ToString("o"));
        await _db.SaveInsightAsync(second);
        var first = Incident("first", "fire", "fire");
        first.IncidentId = await IncidentGrouping.ResolveAsync(_db, "first", "fire", 42.78, -71.23, t0.ToString("o"));

        Assert.Equal("second", first.IncidentId);
    }

    [Fact]
    public async Task UngroupedIncidents_AreListedOldestFirstUntilAssigned()
    {
        var t0 = DateTime.UtcNow.AddHours(-1);
        await _db.SaveTransmissionAsync(Tx("b", t0.AddMinutes(2)));
        await _db.SaveTransmissionAsync(Tx("a", t0));
        await _db.SaveTransmissionAsync(Tx("nogeo", t0));
        await _db.SaveInsightAsync(Incident("b", "fire", "fire"));
        await _db.SaveInsightAsync(Incident("a", "fire", "fire"));
        await _db.SaveInsightAsync(Incident("nogeo", "fire", "fire", null, null));

        var pending = (await _db.GetUngroupedIncidentsAsync()).ToList();
        Assert.Equal(new[] { "a", "b" }, pending.Select(p => p.TransmissionId));
        Assert.Equal("fire", pending[0].Service);

        // Before grouping, a map call is its own incident.
        var since = t0.AddMinutes(-1).ToString("o");
        Assert.Equal("a", (await _db.GetMapCallsAsync(new MapCallFilter(since, null, null, null, 100))).Single(c => c.Id == "a").IncidentId);

        await _db.SetIncidentIdAsync("a", "a");
        await _db.SetIncidentIdAsync("b", "a");
        Assert.Empty(await _db.GetUngroupedIncidentsAsync());
        Assert.Equal("a", (await _db.GetInsightAsync("b"))!.IncidentId);
    }

    [Fact]
    public async Task GetUnanalyzed_SkipsAnalyzedAndUntranscribed()
    {
        var now = DateTime.UtcNow;
        await _db.SaveTransmissionAsync(Tx("done", now));
        await _db.SaveTransmissionAsync(Tx("todo", now.AddMinutes(-1)));
        await _db.SaveTransmissionAsync(Tx("blank", now.AddMinutes(-2), null));
        await _db.SaveInsightAsync(new TransmissionInsight { TransmissionId = "done", AnalyzedAt = now.ToString("o") });

        var pending = (await _db.GetUnanalyzedAsync(10)).ToList();
        Assert.Equal(new[] { "todo" }, pending.Select(l => l.Id));
        Assert.Equal("Engine 2 respond", pending[0].Transcription);
        Assert.Equal(1, await _db.CountUnanalyzedAsync());
    }

    [Fact]
    public async Task History_CarriesIncidentInsights_ButNotRoutineOnes()
    {
        var now = DateTime.UtcNow;
        await _db.SaveTransmissionAsync(Tx("inc", now.AddMinutes(-2)));
        await _db.SaveTransmissionAsync(Tx("chatter", now.AddMinutes(-1)));
        await _db.SaveTransmissionAsync(Tx("plain", now));
        await _db.SaveInsightAsync(Incident("inc", "fire", "fire"));
        await _db.SaveInsightAsync(new TransmissionInsight { TransmissionId = "chatter", AnalyzedAt = now.ToString("o") });

        var history = (await _db.GetHistoryAsync()).ToDictionary(l => l.Id);

        Assert.Equal("fire", history["inc"].Insight?.CallType);
        Assert.True(history["inc"].Insight!.IsIncident);
        Assert.Null(history["chatter"].Insight);
        Assert.Null(history["plain"].Insight);
        Assert.Equal("fire", (await _db.GetTransmissionByIdAsync("inc"))!.Insight?.CallType);
        Assert.Equal("fire", (await _db.SearchTransmissionsAsync("Engine")).FirstOrDefault(l => l.Id == "inc")?.Insight?.CallType);
    }

    [Fact]
    public async Task DeleteTransmission_RemovesItsInsight()
    {
        await _db.SaveTransmissionAsync(Tx("gone", DateTime.UtcNow));
        await _db.SaveInsightAsync(Incident("gone", "fire", "fire"));

        await _db.DeleteTransmissionAsync("gone");

        Assert.Null(await _db.GetInsightAsync("gone"));
    }

    [Fact]
    public async Task QueryTransmissions_PagesWithoutGapsOrDuplicates()
    {
        var t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 25; i++)
        {
            await _db.SaveTransmissionAsync(Tx($"log_{i:D2}", t0.AddMinutes(i)));
        }
        // Two rows sharing a timestamp must still be split cleanly across pages.
        await _db.SaveTransmissionAsync(Tx("log_tie", t0.AddMinutes(14)));

        var seen = new List<string>();
        var pageSizes = new List<int>();
        TransmissionCursor? cursor = null;
        do
        {
            var page = await _db.QueryTransmissionsAsync(new TransmissionQuery { Limit = 10, Cursor = cursor });
            pageSizes.Add(page.Items.Count);
            seen.AddRange(page.Items.Select(l => l.Id));
            cursor = page.NextCursor is null ? null : Decode(page.NextCursor);
        } while (cursor != null);

        Assert.Equal(new[] { 10, 10, 6 }, pageSizes);
        Assert.Equal(26, seen.Distinct().Count());
        Assert.Equal("log_24", seen[0]);
        Assert.Equal("log_00", seen[^1]);
        // Newest first, ties broken by id descending.
        Assert.True(seen.IndexOf("log_tie") < seen.IndexOf("log_14"));
    }

    [Fact]
    public async Task QueryTransmissions_ExactPageHasNoNextCursor()
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++) await _db.SaveTransmissionAsync(Tx($"x{i}", now.AddSeconds(i)));

        var page = await _db.QueryTransmissionsAsync(new TransmissionQuery { Limit = 5 });

        Assert.Equal(5, page.Items.Count);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task QueryTransmissions_AppliesEachFilter()
    {
        var t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        await _db.SaveTransmissionAsync(new CallLog("a", t0.ToString("o"), 154.43, "FD Dispatch", "", null, null, "a.raw", 3, "Engine 2 respond to Main"));
        await _db.SaveTransmissionAsync(new CallLog("b", t0.AddHours(1).ToString("o"), 155.61, "PD Dispatch", "", null, null, "b.raw", 3, "Traffic stop on Elm") { DetectedTone = "Station 1" });
        await _db.SaveTransmissionAsync(new CallLog("c", t0.AddHours(2).ToString("o"), 154.43, "FD Dispatch", "", null, null, "c.raw", 3, "Box alarm"));
        await _db.SaveTransmissionAsync(new CallLog("d", t0.AddHours(3).ToString("o"), 155.61, "PD Dispatch", "", null, null, "d.raw", 3, "Clear"));
        await _db.SetFavoriteAsync("c", true);
        await _db.AddRadioEventAsync(new RadioEvent { Id = "ev1", Timestamp = t0.AddHours(2).ToString("o"), Type = "TONE_OUT", Label = "Station 2", TransmissionId = "c" });
        await _db.AddRadioEventAsync(new RadioEvent { Id = "ev2", Timestamp = t0.AddHours(3).ToString("o"), Type = "MDC_PTT", Label = "Unit 5", TransmissionId = "d" });
        await _db.SaveInsightAsync(Incident("a", "fire", "fire"));

        async Task<string[]> Ids(TransmissionQuery q) =>
            (await _db.QueryTransmissionsAsync(q)).Items.Select(l => l.Id).ToArray();

        Assert.Equal(new[] { "c", "a" }, await Ids(new() { Channel = "FD Dispatch" }));
        Assert.Equal(new[] { "d", "b" }, await Ids(new() { Frequency = 155.6100001 }));
        Assert.Equal(new[] { "c", "b" }, await Ids(new() { SinceUtc = t0.AddHours(1).ToString("o"), UntilUtc = t0.AddHours(3).ToString("o") }));
        Assert.Equal(new[] { "b" }, await Ids(new() { Search = "elm" }));
        Assert.Equal(new[] { "c" }, await Ids(new() { FavoritesOnly = true }));
        // Tone-outs: a detected tone, or referenced by a TONE_OUT event (not other event types).
        Assert.Equal(new[] { "c", "b" }, await Ids(new() { TonesOnly = true }));
        Assert.Equal(new[] { "a" }, await Ids(new() { IncidentsOnly = true }));
        Assert.Equal(new[] { "c" }, await Ids(new() { Channel = "FD Dispatch", FavoritesOnly = true }));

        var batch = await _db.QueryTransmissionsAsync(new TransmissionQuery { Ids = new[] { "a", "d", "missing" }, Limit = 1 });
        Assert.Equal(new[] { "d", "a" }, batch.Items.Select(l => l.Id));
        Assert.Null(batch.NextCursor);
        Assert.Equal("fire", batch.Items.Single(l => l.Id == "a").Insight?.CallType);
    }

    [Fact]
    public async Task GetTransmissions_ByLocalDay_IncludesTheWholeLocalDayOnly()
    {
        var dayStart = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Local);
        await _db.SaveTransmissionAsync(new CallLog("before", dayStart.AddMinutes(-1).ToUniversalTime().ToString("o"), 155.0, "ChanA", "", null, null, null, 1));
        await _db.SaveTransmissionAsync(new CallLog("first", dayStart.ToUniversalTime().ToString("o"), 155.0, "ChanA", "", null, null, null, 1));
        await _db.SaveTransmissionAsync(new CallLog("last", dayStart.AddDays(1).AddMinutes(-1).ToUniversalTime().ToString("o"), 155.0, "ChanA", "", null, null, null, 1));
        await _db.SaveTransmissionAsync(new CallLog("after", dayStart.AddDays(1).ToUniversalTime().ToString("o"), 155.0, "ChanA", "", null, null, null, 1));

        var logs = (await _db.GetTransmissionsAsync("2026", "03", "10", "ChanA", 155.0)).Select(l => l.Id).ToArray();

        Assert.Equal(new[] { "last", "first" }, logs);
        Assert.Empty(await _db.GetTransmissionsAsync("2026", "02", "30", "ChanA", 155.0));
    }

    [Fact]
    public async Task SaveTransmission_WithoutTimestamp_UsesRoundTripFormat()
    {
        var log = new CallLog("no_ts", "", 155.0, "Tag", "", null, null, null, 1);

        await _db.SaveTransmissionAsync(log);

        var saved = await _db.GetTransmissionByIdAsync("no_ts");
        Assert.EndsWith("Z", saved!.Timestamp);
        Assert.Contains("T", saved.Timestamp);
    }

    private static TransmissionCursor Decode(string cursor)
    {
        Assert.True(TransmissionCursor.TryDecode(cursor, out var c));
        return c;
    }

    [Fact]
    public async Task AiFeaturesEnabled_DefaultsToFalse()
    {
        Assert.Equal("false", await _db.GetSettingAsync("AiFeaturesEnabled"));
    }
}
