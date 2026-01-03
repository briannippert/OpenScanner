using Dapper;
using Microsoft.Data.Sqlite;
using OpenScanner.Server.Models;

namespace OpenScanner.Server;

public class Database
{
    private readonly string _connectionString;
    private readonly string _dataDir;

    public Database(string connectionString = "Data Source=../../data/openscanner.db")
    {
        // Adjust path if needed
        var root = Directory.GetCurrentDirectory();
        // Assuming we run from server-net/OpenScanner.Server, data is in ../../data
        // But let's check absolute path behavior or use environment variable.
        // For now, hardcode relative to project root assuming debugging.
        // In production, this should be configurable.
        
        // Ensure data directory exists
        _dataDir = Path.Combine(root, "../../data");
        if (!Directory.Exists(_dataDir)) Directory.CreateDirectory(_dataDir);

        _connectionString = $"Data Source={Path.Combine(_dataDir, "openscanner.db")}";
        
        Initialize();
    }

    private void Initialize()
    {
        using var conn = GetConnection();
        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS transmissions (
                id TEXT PRIMARY KEY,
                timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
                frequency REAL,
                alphaTag TEXT,
                description TEXT,
                lat REAL,
                lon REAL,
                alt REAL,
                audio_path TEXT,
                duration REAL
            );

            CREATE TABLE IF NOT EXISTS channels (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                frequency REAL UNIQUE,
                license TEXT,
                type TEXT,
                tone TEXT,
                alphaTag TEXT,
                description TEXT,
                mode TEXT,
                tag TEXT
            );
        ");

        // Seed if empty
        var count = conn.ExecuteScalar<int>("SELECT count(*) FROM channels");
        if (count == 0)
        {
            var seed = new[]
            {
                new Channel(155.0325, "Salem Police", "Police Operations", "P25", "RM", "117 NAC", "Law Dispatch", "WQGI420"),
                new Channel(155.8875, "Salem Fire", "Fire Operations", "P25", "RM", "117 NAC", "Fire Dispatch", "WPMN513")
            };
            conn.Execute(@"
                INSERT INTO channels (frequency, license, type, tone, alphaTag, description, mode, tag)
                VALUES (@Frequency, @License, @Type, @Tone, @AlphaTag, @Description, @Mode, @Tag)", seed);
        }
    }

    public SqliteConnection GetConnection() => new SqliteConnection(_connectionString);

    public IEnumerable<Channel> GetAllChannels()
    {
        using var conn = GetConnection();
        return conn.Query<Channel>("SELECT * FROM channels ORDER BY frequency ASC");
    }

    public int AddChannel(Channel channel)
    {
        using var conn = GetConnection();
        return conn.ExecuteScalar<int>(@"
            INSERT INTO channels (frequency, license, type, tone, alphaTag, description, mode, tag)
            VALUES (@Frequency, @License, @Type, @Tone, @AlphaTag, @Description, @Mode, @Tag)
            RETURNING id", channel);
    }

    public void UpdateChannel(Channel channel)
    {
        using var conn = GetConnection();
        conn.Execute(@"
            UPDATE channels 
            SET frequency=@Frequency, license=@License, type=@Type, tone=@Tone, 
                alphaTag=@AlphaTag, description=@Description, mode=@Mode, tag=@Tag
            WHERE id=@Id", channel);
    }

    public void DeleteChannel(int id)
    {
        using var conn = GetConnection();
        conn.Execute("DELETE FROM channels WHERE id = @Id", new { Id = id });
    }

    public void SaveTransmission(CallLog log)
    {
        using var conn = GetConnection();
        conn.Execute(@"
            INSERT INTO transmissions (id, timestamp, frequency, alphaTag, description, lat, lon, alt, audio_path, duration)
            VALUES (@Id, datetime('now'), @Frequency, @AlphaTag, @Description, @Lat, @Lon, 0, @AudioPath, @Duration)", log);
    }

    public IEnumerable<CallLog> GetHistory(int limit = 100)
    {
        using var conn = GetConnection();
        return conn.Query<CallLog>("SELECT id, timestamp, frequency, alphaTag, description, lat, lon, alt, audio_path as AudioPath, duration FROM transmissions ORDER BY timestamp DESC LIMIT @Limit", new { Limit = limit });
    }
    
    public void DeleteTransmission(string id)
    {
        using var conn = GetConnection();
        var path = conn.QueryFirstOrDefault<string>("SELECT audio_path FROM transmissions WHERE id = @Id", new { Id = id });
        if (!string.IsNullOrEmpty(path))
        {
            var fullPath = Path.Combine(_dataDir, "recordings", path);
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }
        conn.Execute("DELETE FROM transmissions WHERE id = @Id", new { Id = id });
    }
}
