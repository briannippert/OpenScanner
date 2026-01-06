using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

public class WebSocketBroadcaster
{
    private readonly RtlDevice _radio;
    private readonly ILogger<WebSocketBroadcaster> _logger;
    private readonly ConcurrentDictionary<string, SocketSession> _controlSessions = new();
    private readonly ConcurrentDictionary<string, SocketSession> _audioSessions = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private class SocketSession
    {
        public WebSocket Socket { get; }
        public SemaphoreSlim Lock { get; } = new(1, 1);

        public SocketSession(WebSocket socket)
        {
            Socket = socket;
        }
    }

    public WebSocketBroadcaster(RtlDevice radio, ILogger<WebSocketBroadcaster> logger)
    {
        _radio = radio;
        _logger = logger;
        _radio.OnStateChanged += BroadcastState;
        _radio.OnNewLog += BroadcastLog;
        _radio.OnAudio += BroadcastAudio;
    }

    public async Task HandleControlConnection(WebSocket socket)
    {
        var id = Guid.NewGuid().ToString();
        var session = new SocketSession(socket);
        _controlSessions.TryAdd(id, session);
        _logger.LogInformation($"New Control WebSocket connection: {id}");

        // Send initial state
        var initialState = new { type = "STATE_UPDATE", payload = _radio.GetState() };
        await SendJsonAsync(session, initialState);

        await HandleSessionLoop(socket, id, _controlSessions);
    }

    public async Task HandleAudioConnection(WebSocket socket)
    {
        var id = Guid.NewGuid().ToString();
        var session = new SocketSession(socket);
        _audioSessions.TryAdd(id, session);
        _logger.LogInformation($"New Audio WebSocket connection: {id}");

        await HandleSessionLoop(socket, id, _audioSessions);
    }

    private async Task HandleSessionLoop(WebSocket socket, string id, ConcurrentDictionary<string, SocketSession> sessions)
    {
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
            sessions.TryRemove(id, out _);
            _logger.LogInformation($"WebSocket disconnected: {id}");
        }
    }

    private async Task SendJsonAsync(SocketSession session, object data)
    {
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);
        
        await session.Lock.WaitAsync();
        try
        {
            if (session.Socket.State == WebSocketState.Open)
            {
                await session.Socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        finally
        {
            session.Lock.Release();
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
        // _logger.LogInformation($"Broadcasting audio: {audioData.Length} bytes to {_audioSessions.Count} clients");
        if (_audioSessions.IsEmpty) return;
        _ = BroadcastBinary(audioData);
    }

    private async Task BroadcastJson(object data)
    {
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        var tasks = _controlSessions.Values.Select(async session =>
        {
            await session.Lock.WaitAsync();
            try
            {
                if (session.Socket.State == WebSocketState.Open)
                {
                    await session.Socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch { }
            finally
            {
                session.Lock.Release();
            }
        });
        
        await Task.WhenAll(tasks);
    }

    private async Task BroadcastBinary(byte[] data)
    {
        var segment = new ArraySegment<byte>(data);
        var tasks = _audioSessions.Values.Select(async session =>
        {
            await session.Lock.WaitAsync();
            try
            {
                if (session.Socket.State == WebSocketState.Open)
                {
                    await session.Socket.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None);
                }
            }
            catch { }
            finally
            {
                session.Lock.Release();
            }
        });
        
        await Task.WhenAll(tasks);
    }
}
