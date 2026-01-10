using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

/// <summary>
/// Background service that connects to gpsd to provide real-time GPS location data.
/// </summary>
public class GpsService : BackgroundService
{
    private readonly ILogger<GpsService> _logger;
    private GpsData? _lastGps;
    private DateTime _lastUpdate = DateTime.MinValue;

    /// <summary>
    /// Event triggered when a new GPS location update is received.
    /// </summary>
    public event Action<GpsData>? OnGpsUpdate;

    /// <summary>
    /// Initializes a new instance of the <see cref="GpsService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public GpsService(ILogger<GpsService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Retrieves the last known GPS location.
    /// </summary>
    /// <returns>The last valid GPS data, or null if stale (>10s) or unavailable.</returns>
    public GpsData? GetLastLocation() 
    {
        // If data is older than 10 seconds, consider it stale
        if (DateTime.UtcNow - _lastUpdate > TimeSpan.FromSeconds(10)) return null;
        return _lastGps;
    }

    /// <summary>
    /// Executes the long-running task to monitor gpsd.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                // gpsd default port is 2947
                await client.ConnectAsync("localhost", 2947, stoppingToken);
                _logger.LogInformation("Connected to gpsd");

                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

                // Enable JSON streaming
                await writer.WriteLineAsync("?WATCH={\"enable\":true,\"json\":true}");

                // Request 10Hz update rate (0.1s cycle)
                await writer.WriteLineAsync("?DEVICE={\"native\":1,\"cycle\":0.1}"); 

                while (client.Connected && !stoppingToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(stoppingToken);
                    if (string.IsNullOrEmpty(line)) continue;
                    
                    try 
                    {
                        var json = JsonDocument.Parse(line);
                        if (json.RootElement.TryGetProperty("class", out var cls))
                        {
                            var type = cls.GetString();
                            if (type == "TPV")
                            {
                                ParseTpv(json.RootElement);
                            }
                            else if (type == "SKY")
                            {
                                ParseSky(json.RootElement);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "GPSD JSON parse error");
                    }
                }
            }
            catch (Exception ex)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogDebug("GPSD connection task cancelled");
                }
                else
                {
                    _logger.LogWarning("GPSD Connection failed (retrying in 5s): " + ex.Message);
                }
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    internal void ParseSky(JsonElement root)
    {
        // Count satellites used in solution (u=true)
        int satsUsed = 0;
        int totalSeen = 0;
        if (root.TryGetProperty("satellites", out var satellites) && satellites.ValueKind == JsonValueKind.Array)
        {
            foreach (var sat in satellites.EnumerateArray())
            {
                totalSeen++;
                if (sat.TryGetProperty("used", out var used) && used.GetBoolean())
                {
                    satsUsed++;
                }
            }
        }

        // Only update if we actually got satellite info to prevent flickering to 0
        if (totalSeen == 0) return;

        // Update state preserving other data
        if (_lastGps != null)
        {
            _lastGps = _lastGps with { Sats = satsUsed, SatsVisible = totalSeen };
        }
        else
        {
            _lastGps = new GpsData(0, 0, 0, 0, "", 0, satsUsed, totalSeen);
        }
        OnGpsUpdate?.Invoke(_lastGps);
    }

    internal void ParseTpv(JsonElement root)
    {
        // "mode": 0 (n/a), 1 (no fix), 2 (2D), 3 (3D)
        int mode = root.TryGetProperty("mode", out var modeProp) ? modeProp.GetInt32() : 0;
        bool hasFix = mode >= 2;
        bool wasFixed = (_lastGps?.Fix ?? 0) >= 2;

        if (hasFix && !wasFixed)
        {
            _logger.LogInformation($"GPS Fix Acquired ({mode}D)");
        }
        else if (!hasFix && wasFixed)
        {
            _logger.LogWarning("GPS Fix Lost");
        }

        if (!hasFix) 
        {
            if (_lastGps != null && _lastGps.Fix != mode)
            {
                _lastGps = _lastGps with { Fix = mode };
                OnGpsUpdate?.Invoke(_lastGps);
            }
            return;
        }
        
        double lat = root.TryGetProperty("lat", out var l) ? l.GetDouble() : (_lastGps?.Lat ?? 0);
        double lon = root.TryGetProperty("lon", out var ln) ? ln.GetDouble() : (_lastGps?.Lon ?? 0);
        double alt = root.TryGetProperty("alt", out var a) ? a.GetDouble() : (_lastGps?.Alt ?? 0);
        double speed = root.TryGetProperty("speed", out var s) ? s.GetDouble() : 0;
        string time = root.TryGetProperty("time", out var t) ? t.GetString() ?? "" : "";
        
        // Horizontal Dilution of Precision
        double? hdop = null;
        if (root.TryGetProperty("hdop", out var h)) hdop = h.GetDouble();

        // Preserve previous satellite counts
        int currentSats = _lastGps?.Sats ?? 0;
        int? currentVisible = _lastGps?.SatsVisible;

        var gps = new GpsData(lat, lon, alt, speed, time, modeProp.GetInt32(), currentSats, currentVisible, hdop);
        _lastGps = gps;
        _lastUpdate = DateTime.UtcNow;
        OnGpsUpdate?.Invoke(gps);
    }
}
