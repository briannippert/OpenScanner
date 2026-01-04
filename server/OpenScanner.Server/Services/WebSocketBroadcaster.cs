using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

public class WebSocketBroadcaster
{
    private readonly RtlDevice _radio;
    private readonly ConcurrentDictionary<string, WebSocket> _sockets = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public WebSocketBroadcaster(RtlDevice radio)
    {
        _radio = radio;
        _radio.OnStateChanged += BroadcastState;
        _radio.OnNewLog += BroadcastLog;
        _radio.OnAudio += BroadcastAudio;
    }

    public async Task HandleConnection(WebSocket socket)
    {
        var id = Guid.NewGuid().ToString();
        _sockets.TryAdd(id, socket);

        // Send initial state
        var initialState = new { type = "STATE_UPDATE", payload = _radio.GetState() };
        await SendJsonAsync(socket, initialState);

        // Keep connection open until closed by client
        var buffer = new byte[1024 * 4];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
                }
            }
        }
        catch
        {
            // Ignored
        }
        finally
        {
            _sockets.TryRemove(id, out _);
        }
    }

    private async Task SendJsonAsync(WebSocket socket, object data)
    {
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);
        
        if (socket.State == WebSocketState.Open)
        {
             await socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    private void BroadcastState(ScannerState state)
    {
        var msg = new { type = "STATE_UPDATE", payload = state };
        _ = BroadcastJson(msg);
    }

    private void BroadcastLog(CallLog log)
    {
        var msg = new { type = "NEW_LOG", payload = log };
        _ = BroadcastJson(msg);
    }

    private void BroadcastAudio(byte[] audioData)
    {
        _ = BroadcastBinary(audioData);
    }

    private async Task BroadcastJson(object data)
    {
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        foreach (var socket in _sockets.Values)
        {
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch
                {
                    // Handle broken sockets?
                }
            }
        }
    }

    private async Task BroadcastBinary(byte[] data)
    {
        var segment = new ArraySegment<byte>(data);
        foreach (var socket in _sockets.Values)
        {
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None);
                }
                catch
                {
                    // Handle broken sockets
                }
            }
        }
    }
}
