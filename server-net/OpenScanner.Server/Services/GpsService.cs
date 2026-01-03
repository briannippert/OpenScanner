using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

public class GpsService : BackgroundService
{
    private readonly ILogger<GpsService> _logger;
    private GpsData? _lastGps;

    public event Action<GpsData>? OnGpsUpdate;

    public GpsService(ILogger<GpsService> logger)
    {
        _logger = logger;
    }

    public GpsData? GetLastLocation() => _lastGps;

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
                    catch (Exception)
                    {
                        // Ignore parse errors
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("GPSD Connection failed (retrying in 5s): " + ex.Message);
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

        _logger.LogInformation($"GPS SKY: Seen {totalSeen}, Used {satsUsed}");

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
        // "mode": 2 (2D), 3 (3D)
        if (!root.TryGetProperty("mode", out var modeProp) || modeProp.GetInt32() < 2) return;
        
        double lat = root.TryGetProperty("lat", out var l) ? l.GetDouble() : (_lastGps?.Lat ?? 0);
        double lon = root.TryGetProperty("lon", out var ln) ? ln.GetDouble() : (_lastGps?.Lon ?? 0);
        double alt = root.TryGetProperty("alt", out var a) ? a.GetDouble() : (_lastGps?.Alt ?? 0);
        double speed = root.TryGetProperty("speed", out var s) ? s.GetDouble() : 0;
        string time = root.TryGetProperty("time", out var t) ? t.GetString() ?? "" : "";

        // Preserve previous satellite counts
        int currentSats = _lastGps?.Sats ?? 0;
        int? currentVisible = _lastGps?.SatsVisible;

        var gps = new GpsData(lat, lon, alt, speed, time, modeProp.GetInt32(), currentSats, currentVisible);
        _lastGps = gps;
        OnGpsUpdate?.Invoke(gps);
    }
}
