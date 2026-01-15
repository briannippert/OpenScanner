using Dapper;
using Microsoft.Data.Sqlite;
using OpenScanner.Server.Models;
using Microsoft.Extensions.Configuration;
using OpenScanner.Server.Interfaces;

namespace OpenScanner.Server;

/// <summary>
/// SQLite implementation of the database interface.
/// </summary>
public class Database : IDatabase
{
    private readonly string _connectionString;
    private readonly string _dataDir;
    private readonly ILogger<Database> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Database"/> class.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="logger">Logger instance.</param>
    public Database(IConfiguration configuration, ILogger<Database> logger)
    {
        _logger = logger;
        var root = Directory.GetCurrentDirectory();
        
        var configDataDir = configuration["Database:DataDir"];
        if (!string.IsNullOrEmpty(configDataDir))
        {
            _dataDir = Path.IsPathRooted(configDataDir) ? configDataDir : Path.GetFullPath(Path.Combine(root, configDataDir));
        }
        else
        {
            _dataDir = Path.GetFullPath(Path.Combine(root, "../../data"));
        }

        var configConnString = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrEmpty(configConnString))
        {
            _connectionString = configConnString;
            
            if (_connectionString.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
            {
                var path = _connectionString.Substring("Data Source=".Length).Split(';')[0];
                if (!string.IsNullOrEmpty(path) && path != ":memory:")
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (dir != null) _dataDir = dir;
                }
            }
        }
        else
        {
            if (!Directory.Exists(_dataDir)) Directory.CreateDirectory(_dataDir);
            _connectionString = $"Data Source={Path.Combine(_dataDir, "openscanner.db")}";
        }
        
        Initialize();
    }

    private void Initialize()
    {
        using var conn = GetConnection();
        var sql = SqlLoader.GetSql("Initialize.sql");
        conn.Execute(sql);
        
        try { conn.Execute("ALTER TABLE transmissions ADD COLUMN transcription TEXT;"); } catch (Exception ex) { _logger.LogDebug(ex, "Migration skip: transcription column already exists or failed"); }
        try { conn.Execute("ALTER TABLE transmissions ADD COLUMN sourceID INTEGER;"); } catch (Exception ex) { _logger.LogDebug(ex, "Migration skip: sourceID column already exists or failed"); }
        try { conn.Execute("ALTER TABLE transmissions ADD COLUMN targetID INTEGER;"); } catch (Exception ex) { _logger.LogDebug(ex, "Migration skip: targetID column already exists or failed"); }
        try { conn.Execute("ALTER TABLE transmissions ADD COLUMN detectedTone TEXT;"); } catch (Exception ex) { _logger.LogDebug(ex, "Migration skip: detectedTone column already exists or failed"); }
        try { conn.Execute("ALTER TABLE channels ADD COLUMN lat REAL;"); } catch (Exception ex) { _logger.LogDebug(ex, "Migration skip: lat column already exists or failed"); }
        try { conn.Execute("ALTER TABLE channels ADD COLUMN lon REAL;"); } catch (Exception ex) { _logger.LogDebug(ex, "Migration skip: lon column already exists or failed"); }
        try { conn.Execute("ALTER TABLE channels ADD COLUMN range REAL;"); } catch (Exception ex) { _logger.LogDebug(ex, "Migration skip: range column already exists or failed"); }
        try { conn.Execute("ALTER TABLE channels ADD COLUMN avoid INTEGER DEFAULT 0;"); } catch (Exception ex) { _logger.LogDebug(ex, "Migration skip: avoid column already exists or failed"); }

        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS fire_tones (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT,
                frequencyA REAL,
                frequencyB REAL,
                description TEXT
            );
        ");

        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT
            );
        ");

        var settingsCount = conn.ExecuteScalar<int>("SELECT count(*) FROM settings WHERE key = 'EnableTranscription'");
        if (settingsCount == 0)
        {
            conn.Execute("INSERT INTO settings (key, value) VALUES ('EnableTranscription', 'true')");
        }

        var count = conn.ExecuteScalar<int>("SELECT count(*) FROM channels");
        if (count == 0)
        {
            var seed = new[]
            {
                new Channel(155.0325, "Salem Police", "Police Operations", "P25", "RM", "117 NAC", "Law Dispatch", "WQGI420"),
                new Channel(155.8875, "Salem Fire", "Fire Operations", "P25", "RM", "117 NAC", "Fire Dispatch", "WPMN513")
            };
            var seedSql = SqlLoader.GetSql("Channels/Seed.sql");
            conn.Execute(seedSql, seed);
        }
    }

    /// <inheritdoc />
    public SqliteConnection GetConnection() => new SqliteConnection(_connectionString);

    /// <inheritdoc />
    public async Task<IEnumerable<Channel>> GetAllChannelsAsync()
    {
        using var conn = GetConnection();
        return await conn.QueryAsync<Channel>(SqlLoader.GetSql("Channels/GetAll.sql"));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Channel>> GetChannelsNearAsync(double lat, double lon)
    {
        using var conn = GetConnection();
        var all = await conn.QueryAsync<Channel>(SqlLoader.GetSql("Channels/GetWithGps.sql"));
        
        return all.Where(c => 
        {
            if (!c.Lat.HasValue || !c.Lon.HasValue) return false;
            double distance = CalculateDistance(lat, lon, c.Lat.Value, c.Lon.Value);
            return distance <= (c.Range ?? 25);
        }).OrderBy(c => c.Frequency);
    }

    private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        var d1 = lat1 * (Math.PI / 180.0);
        var num1 = lon1 * (Math.PI / 180.0);
        var d2 = lat2 * (Math.PI / 180.0);
        var num2 = lon2 * (Math.PI / 180.0) - num1;
        var d3 = Math.Pow(Math.Sin((d2 - d1) / 2.0), 2.0) + Math.Cos(d1) * Math.Cos(d2) * Math.Pow(Math.Sin(num2 / 2.0), 2.0);
        return 6376500.0 * (2.0 * Math.Atan2(Math.Sqrt(d3), Math.Sqrt(1.0 - d3))) * 0.000621371;
    }

    /// <inheritdoc />
    public async Task<int> AddChannelAsync(Channel channel)
    {
        using var conn = GetConnection();
        return await conn.ExecuteScalarAsync<int>(SqlLoader.GetSql("Channels/Insert.sql"), channel);
    }

    /// <inheritdoc />
    public async Task UpdateChannelAsync(Channel channel)
    {
        using var conn = GetConnection();
        await conn.ExecuteAsync(SqlLoader.GetSql("Channels/Update.sql"), channel);
    }

    /// <inheritdoc />
    public async Task DeleteChannelAsync(int id)
    {
        using var conn = GetConnection();
        await conn.ExecuteAsync(SqlLoader.GetSql("Channels/Delete.sql"), new { Id = id });
    }

    /// <inheritdoc />
    public async Task SaveTransmissionAsync(CallLog log)
    {
        using var conn = GetConnection();
        if (string.IsNullOrEmpty(log.Timestamp))
        {
            log.Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        }

        await conn.ExecuteAsync(SqlLoader.GetSql("Transmissions/Insert.sql"), log);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<CallLog>> GetHistoryAsync(int limit = 100)
    {
        using var conn = GetConnection();
        return await conn.QueryAsync<CallLog>(SqlLoader.GetSql("Transmissions/GetHistory.sql"), new { Limit = limit });
    }
    
    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetTransmissionYearsAsync()
    {
        using var conn = GetConnection();
        return await conn.QueryAsync<string>(SqlLoader.GetSql("Transmissions/GetYears.sql"));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetTransmissionMonthsAsync(string year)
    {
        using var conn = GetConnection();
        return await conn.QueryAsync<string>(SqlLoader.GetSql("Transmissions/GetMonths.sql"), new { Year = year });
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetTransmissionDaysAsync(string year, string month)
    {
        using var conn = GetConnection();
        return await conn.QueryAsync<string>(SqlLoader.GetSql("Transmissions/GetDays.sql"), new { Year = year, Month = month });
    }

    /// <inheritdoc />
    public async Task<IEnumerable<dynamic>> GetTransmissionChannelsAsync(string year, string month, string day)
    {
        using var conn = GetConnection();
        return await conn.QueryAsync(SqlLoader.GetSql("Transmissions/GetChannels.sql"), new { Year = year, Month = month, Day = day });
    }

    /// <inheritdoc />
    public async Task<IEnumerable<CallLog>> GetTransmissionsAsync(string year, string month, string day, string alphaTag, double frequency)
    {
        using var conn = GetConnection();
        return await conn.QueryAsync<CallLog>(SqlLoader.GetSql("Transmissions/GetFiltered.sql"), 
            new { Year = year, Month = month, Day = day, AlphaTag = alphaTag, Frequency = frequency });
    }

    /// <inheritdoc />
    public async Task<IEnumerable<CallLog>> SearchTransmissionsAsync(string query)
    {
        using var conn = GetConnection();
        var searchTerm = $"%{query}%";
        return await conn.QueryAsync<CallLog>(SqlLoader.GetSql("Transmissions/Search.sql"), new { Query = searchTerm });
    }

    /// <inheritdoc />
    public async Task DeleteTransmissionAsync(string id)
    {
        using var conn = GetConnection();
        var path = await conn.QueryFirstOrDefaultAsync<string>(SqlLoader.GetSql("Transmissions/GetAudioPath.sql"), new { Id = id });
        if (!string.IsNullOrEmpty(path))
        {
            var fullPath = Path.Combine(_dataDir, "recordings", path);
            if (File.Exists(fullPath)) 
            {
                try { File.Delete(fullPath); } 
                catch (Exception ex) { _logger.LogError(ex, "Failed to delete audio file {Path}", fullPath); }
            }
        }
        await conn.ExecuteAsync(SqlLoader.GetSql("Transmissions/Delete.sql"), new { Id = id });
    }

    /// <inheritdoc />
    public async Task ClearHistoryAsync()
    {
        using var conn = GetConnection();
        await conn.ExecuteAsync("DELETE FROM transmissions");
        
        // Delete all audio files
        var recordingsDir = Path.Combine(_dataDir, "recordings");
        if (Directory.Exists(recordingsDir))
        {
            var files = Directory.GetFiles(recordingsDir);
            foreach (var file in files)
            {
                try { File.Delete(file); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete audio file during history clear: {Path}", file); }
            }
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<FireToneSet>> GetAllFireTonesAsync()
    {
        using var conn = GetConnection();
        return await conn.QueryAsync<FireToneSet>("SELECT * FROM fire_tones");
    }

    /// <inheritdoc />
    public async Task<int> AddFireToneAsync(FireToneSet tone)
    {
        using var conn = GetConnection();
        return await conn.ExecuteScalarAsync<int>("INSERT INTO fire_tones (name, frequencyA, frequencyB, description) VALUES (@Name, @FrequencyA, @FrequencyB, @Description); SELECT last_insert_rowid();", tone);
    }

    /// <inheritdoc />
    public async Task UpdateFireToneAsync(FireToneSet tone)
    {
        using var conn = GetConnection();
        await conn.ExecuteAsync("UPDATE fire_tones SET name=@Name, frequencyA=@FrequencyA, frequencyB=@FrequencyB, description=@Description WHERE id=@Id", tone);
    }

    /// <inheritdoc />
    public async Task DeleteFireToneAsync(int id)
    {
        using var conn = GetConnection();
        await conn.ExecuteAsync("DELETE FROM fire_tones WHERE id=@Id", new { Id = id });
    }

    /// <inheritdoc />
    public async Task<string?> GetSettingAsync(string key)
    {
        using var conn = GetConnection();
        return await conn.QueryFirstOrDefaultAsync<string>("SELECT value FROM settings WHERE key = @Key", new { Key = key });
    }

    /// <inheritdoc />
    public async Task SetSettingAsync(string key, string value)
    {
        using var conn = GetConnection();
        await conn.ExecuteAsync("INSERT OR REPLACE INTO settings (key, value) VALUES (@Key, @Value)", new { Key = key, Value = value });
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, string>> GetAllSettingsAsync()
    {
        using var conn = GetConnection();
        var result = await conn.QueryAsync<(string Key, string Value)>("SELECT key, value FROM settings");
        return result.ToDictionary(x => x.Key, x => x.Value);
    }
}
