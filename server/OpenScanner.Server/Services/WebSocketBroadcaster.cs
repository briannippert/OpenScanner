using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenScanner.Server.Models;
using OpenScanner.Server.Interfaces;

namespace OpenScanner.Server.Services;

/// <summary>
/// Service that manages WebSocket connections and broadcasts real-time updates to clients.
/// </summary>
public class WebSocketBroadcaster
{
    private readonly IRadioSource _radio;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketBroadcaster"/> class.
    /// </summary>
    /// <param name="radio">The radio source interface.</param>
    /// <param name="logger">The logger instance.</param>
    public WebSocketBroadcaster(IRadioSource radio, ILogger<WebSocketBroadcaster> logger)
    {
        _radio = radio;
        _logger = logger;
        _radio.OnStateChanged += BroadcastState;
        _radio.OnNewLog += BroadcastLog;
        _radio.OnNewEvent += BroadcastEvent;
        _radio.OnAudio += BroadcastAudio;
    }

    /// <summary>
    /// Handles a new WebSocket connection for control messages (state updates, logs).
    /// </summary>
    /// <param name="socket">The WebSocket connection.</param>
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

    /// <summary>
    /// Handles a new WebSocket connection for streaming audio data.
    /// </summary>
    /// <param name="socket">The WebSocket connection.</param>
    public async Task HandleAudioConnection(WebSocket socket)
    {
        var id = Guid.NewGuid().ToString();
        var session = new SocketSession(socket);
        _audioSessions.TryAdd(id, session);
        _logger.LogInformation($"New Audio WebSocket connection: {id}");

        // Send pre-roll buffer to catch up new clients with the beginning of the transmission
        var preRollData = _radio.GetPreRollBuffer();
        foreach (var chunk in preRollData)
        {
            try
            {
                await session.Lock.WaitAsync();
                if (socket.State == WebSocketState.Open)
                {
                    var segment = new ArraySegment<byte>(chunk);
                    await socket.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None);
                }
                session.Lock.Release();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send pre-roll audio chunk to WebSocket {Id}", id);
                session.Lock.Release();
            }
        }

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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WebSocket session loop error for {Id}", id);
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

    private DateTime _lastStateBroadcast = DateTime.MinValue;
    private ScannerState? _lastSentState;

    private void BroadcastState(ScannerState state)
    {
        var now = DateTime.UtcNow;
        
        // Always send if critical state changed (Status or Connection)
        bool criticalUpdate = _lastSentState == null || 
                              _lastSentState.Status != state.Status || 
                              _lastSentState.IsHardwareConnected != state.IsHardwareConnected;

        if (!criticalUpdate && (now - _lastStateBroadcast).TotalMilliseconds < 100) 
        {
            return;
        }

        _lastStateBroadcast = now;
        _lastSentState = state;

        var msg = new { type = "STATE_UPDATE", payload = state };
        _ = BroadcastJson(msg);
    }

    private void BroadcastLog(CallLog log)
    {
        var msg = new { type = "NEW_LOG", payload = log };
        _ = BroadcastJson(msg);
    }

    private void BroadcastEvent(RadioEvent e)
    {
        var msg = new { type = "NEW_EVENT", payload = e };
        _ = BroadcastJson(msg);
    }

    private void BroadcastAudio(byte[] audioData)
    {
        // _logger.LogInformation($"Broadcasting audio: {audioData.Length} bytes to {_audioSessions.Count} clients");
        if (_audioSessions.IsEmpty) 
        {
            // _logger.LogWarning("Audio generated but no clients connected.");
            return;
        }
        
        // Log occasionally to confirm flow
        if (DateTime.UtcNow.Second % 5 == 0 && DateTime.UtcNow.Millisecond < 100)
        {
             _logger.LogInformation($"[WebSocket] Sending {audioData.Length} bytes to {_audioSessions.Count} clients");
        }

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
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send JSON to a WebSocket session");
            }
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
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send binary to a WebSocket session");
            }
            finally
            {
                session.Lock.Release();
            }
        });
        
        await Task.WhenAll(tasks);
    }
}
