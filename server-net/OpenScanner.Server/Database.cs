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
                duration REAL,
                transcription TEXT
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
                tag TEXT,
                lat REAL,
                lon REAL,
                range REAL
            );
        ");
        
        // Simple migration for existing table
        try { conn.Execute("ALTER TABLE transmissions ADD COLUMN transcription TEXT;"); } catch {}
        try { conn.Execute("ALTER TABLE channels ADD COLUMN lat REAL;"); } catch {}
        try { conn.Execute("ALTER TABLE channels ADD COLUMN lon REAL;"); } catch {}
        try { conn.Execute("ALTER TABLE channels ADD COLUMN range REAL;"); } catch {}

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

    public IEnumerable<Channel> GetChannelsNear(double lat, double lon)
    {
        using var conn = GetConnection();
        var all = conn.Query<Channel>("SELECT * FROM channels WHERE lat IS NOT NULL AND lon IS NOT NULL");
        
        // Filter by distance in C# (easier than SQLite math functions)
        return all.Where(c => 
        {
            if (!c.Lat.HasValue || !c.Lon.HasValue) return false;
            double distance = CalculateDistance(lat, lon, c.Lat.Value, c.Lon.Value);
            return distance <= (c.Range ?? 25); // Default to 25 mile range
        }).OrderBy(c => c.Frequency);
    }

    private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        var d1 = lat1 * (Math.PI / 180.0);
        var num1 = lon1 * (Math.PI / 180.0);
        var d2 = lat2 * (Math.PI / 180.0);
        var num2 = lon2 * (Math.PI / 180.0) - num1;
        var d3 = Math.Pow(Math.Sin((d2 - d1) / 2.0), 2.0) + Math.Cos(d1) * Math.Cos(d2) * Math.Pow(Math.Sin(num2 / 2.0), 2.0);
        return 6376500.0 * (2.0 * Math.Atan2(Math.Sqrt(d3), Math.Sqrt(1.0 - d3))) * 0.000621371; // result in miles
    }

    public int AddChannel(Channel channel)
    {
        using var conn = GetConnection();
        return conn.ExecuteScalar<int>(@"
            INSERT INTO channels (frequency, license, type, tone, alphaTag, description, mode, tag, lat, lon, range)
            VALUES (@Frequency, @License, @Type, @Tone, @AlphaTag, @Description, @Mode, @Tag, @Lat, @Lon, @Range)
            RETURNING id", channel);
    }

    public void UpdateChannel(Channel channel)
    {
        using var conn = GetConnection();
        conn.Execute(@"
            UPDATE channels 
            SET frequency=@Frequency, license=@License, type=@Type, tone=@Tone, 
                alphaTag=@AlphaTag, description=@Description, mode=@Mode, tag=@Tag,
                lat=@Lat, lon=@Lon, range=@Range
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
            INSERT INTO transmissions (id, timestamp, frequency, alphaTag, description, lat, lon, alt, audio_path, duration, transcription)
            VALUES (@Id, datetime('now'), @Frequency, @AlphaTag, @Description, @Lat, @Lon, 0, @AudioPath, @Duration, @Transcription)", log);
    }

    public IEnumerable<CallLog> GetHistory(int limit = 100)
    {
        using var conn = GetConnection();
        return conn.Query<CallLog>("SELECT id, timestamp, frequency, alphaTag, description, lat, lon, alt, audio_path as AudioPath, duration, transcription FROM transmissions ORDER BY timestamp DESC LIMIT @Limit", new { Limit = limit });
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
