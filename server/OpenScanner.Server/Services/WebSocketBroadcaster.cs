using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenScanner.Server.Audio;
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

    /// <summary>Number of connected control (state/log) WebSocket clients.</summary>
    public int ControlClientCount => _controlSessions.Count;

    /// <summary>Number of connected audio-streaming WebSocket clients.</summary>
    public int AudioClientCount => _audioSessions.Count;

    private class SocketSession
    {
        public WebSocket Socket { get; }
        public SemaphoreSlim Lock { get; } = new(1, 1);

        /// <summary>What this session negotiated. Control sessions are always <see cref="LiveAudioCodec.LegacyPcm"/>.</summary>
        public LiveAudioCodec Codec { get; }

        /// <summary>
        /// The format most recently announced to this session, so a client joining mid-stream and a
        /// client present across a mono/stereo switch are both told before the samples arrive.
        /// </summary>
        public (int Channels, int SampleRate)? AnnouncedFormat { get; set; }

        public SocketSession(WebSocket socket, LiveAudioCodec codec = LiveAudioCodec.LegacyPcm)
        {
            Socket = socket;
            Codec = codec;
        }
    }

    /// <summary>Bitrate for mono Opus streams (the single-channel scan path).</summary>
    private const int OpusMonoBitrate = 24000;

    /// <summary>Bitrate for stereo Opus streams (parallel FastScan). The two panned talkers are
    /// distinct sources, so joint-stereo coding saves less than it would on real stereo.</summary>
    private const int OpusStereoBitrate = 40000;

    /// <summary>
    /// A gap this long means a transmission ended. The encoder is reset so it doesn't carry
    /// prediction state — or a stranded partial frame — from one transmission into the next.
    /// </summary>
    private static readonly TimeSpan EncoderGapReset = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Shared encoder for the live stream: encode once, send the same bytes to every Opus session.
    /// Guarded by <see cref="_encoderLock"/> because <see cref="OpusStreamEncoder"/> is not
    /// thread-safe and audio arrives from two producer threads (the decoder's stdout reader and
    /// the parallel mixer's timer).
    /// </summary>
    private OpusStreamEncoder? _liveEncoder;
    private readonly object _encoderLock = new();
    private DateTime _lastAudioAt = DateTime.MinValue;

    /// <summary>Whether the server can encode Opus. False disables negotiation of it.</summary>
    public bool OpusAvailable { get; private set; } = true;

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
    /// <param name="codec">The codec negotiated from the <c>?codec=</c> query parameter.</param>
    public async Task HandleAudioConnection(WebSocket socket, LiveAudioCodec codec = LiveAudioCodec.LegacyPcm)
    {
        var id = Guid.NewGuid().ToString();
        var session = new SocketSession(socket, codec);
        _logger.LogInformation("New Audio WebSocket connection: {Id} ({Codec})", id, codec);

        // The pre-roll only ever holds mono: it is fed by the single-channel decoder path and
        // cleared when decoding starts, so the parallel mixer never contributes to it.
        var preRollData = _radio.GetPreRollBuffer();

        if (codec == LiveAudioCodec.LegacyPcm)
        {
            // Pre-negotiation clients get exactly the old behaviour, including the old ordering:
            // registered first, so live frames can interleave into the pre-roll replay. Preserving
            // that quirk is what makes "byte-identical for existing clients" literally true.
            _audioSessions.TryAdd(id, session);
            foreach (var chunk in preRollData)
            {
                await SendFramesAsync(session, id, new[] { (chunk, WebSocketMessageType.Binary) });
            }
        }
        else
        {
            // Negotiated clients are registered only after the pre-roll has been flushed, so the
            // replay is contiguous — two interleaved Opus streams would be audibly broken.
            await AnnounceFormatAsync(session, id, channels: 1, sampleRate: 48000);
            var frames = BuildPreRollFrames(session, preRollData, id);
            if (frames.Count > 0) await SendFramesAsync(session, id, frames);
            _audioSessions.TryAdd(id, session);
        }

        await HandleSessionLoop(socket, id, _audioSessions);
    }

    /// <summary>
    /// Renders the pre-roll into frames for a negotiated session. Opus sessions get the buffer
    /// re-encoded by a throwaway encoder — the seam where it hands off to the shared live encoder
    /// looks like a single lost packet to the decoder, which costs at most one 20 ms frame of
    /// smearing.
    /// </summary>
    private List<(byte[] Data, WebSocketMessageType Type)> BuildPreRollFrames(
        SocketSession session, byte[][] preRollData, string id)
    {
        var frames = new List<(byte[], WebSocketMessageType)>();
        if (preRollData.Length == 0) return frames;

        if (session.Codec != LiveAudioCodec.Opus)
        {
            foreach (var chunk in preRollData) frames.Add((chunk, WebSocketMessageType.Binary));
            return frames;
        }

        try
        {
            using var encoder = new OpusStreamEncoder(channels: 1, bitrate: OpusMonoBitrate);
            foreach (var chunk in preRollData)
            {
                foreach (var packet in encoder.Push(chunk)) frames.Add((packet, WebSocketMessageType.Binary));
            }
            var tail = encoder.FlushWithSilence();
            if (tail != null) frames.Add((tail, WebSocketMessageType.Binary));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to encode pre-roll for WebSocket {Id}; skipping it", id);
            frames.Clear();
        }

        return frames;
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

    /// <summary>
    /// Broadcasts a self-update progress line to control clients as
    /// <c>{ type: "UPDATE_PROGRESS", payload: { phase, line, state } }</c>.
    /// </summary>
    public void BroadcastUpdateProgress(string phase, string line, string state)
    {
        var msg = new { type = "UPDATE_PROGRESS", payload = new { phase, line, state } };
        _ = BroadcastJson(msg);
    }

    private void BroadcastAudio(AudioChunk chunk)
    {
        if (_audioSessions.IsEmpty)
        {
            // Nobody is listening, so don't spend CPU encoding. The encoder is reset lazily on the
            // next chunk via the gap check below.
            return;
        }

        // Log occasionally to confirm flow
        if (DateTime.UtcNow.Second % 5 == 0 && DateTime.UtcNow.Millisecond < 100)
        {
            _logger.LogInformation($"[WebSocket] Sending {chunk.Pcm.Length} bytes to {_audioSessions.Count} clients");
        }

        var packets = EncodeForOpusSessions(chunk);

        var tasks = _audioSessions
            .Select(kvp => SendAudioChunkAsync(kvp.Value, kvp.Key, chunk, packets))
            .ToList();
        _ = Task.WhenAll(tasks);
    }

    /// <summary>
    /// Encodes a chunk for the shared live Opus stream, or returns null when no session wants it.
    /// </summary>
    private IReadOnlyList<byte[]>? EncodeForOpusSessions(AudioChunk chunk)
    {
        bool anyOpus = false;
        foreach (var session in _audioSessions.Values)
        {
            if (session.Codec == LiveAudioCodec.Opus) { anyOpus = true; break; }
        }
        if (!anyOpus) return null;

        lock (_encoderLock)
        {
            try
            {
                var now = DateTime.UtcNow;
                bool gap = now - _lastAudioAt > EncoderGapReset;
                _lastAudioAt = now;

                if (_liveEncoder == null || _liveEncoder.Channels != chunk.Channels)
                {
                    _liveEncoder?.Dispose();
                    _liveEncoder = new OpusStreamEncoder(
                        chunk.Channels,
                        chunk.Channels == 1 ? OpusMonoBitrate : OpusStereoBitrate);
                }
                else if (gap)
                {
                    _liveEncoder.Reset();
                }

                return _liveEncoder.Push(chunk.Pcm);
            }
            catch (Exception ex)
            {
                // Never let an encoder fault take down live audio; drop to PCM for everyone.
                _logger.LogError(ex, "Opus encoding failed; disabling Opus for new connections");
                _liveEncoder?.Dispose();
                _liveEncoder = null;
                OpusAvailable = false;
                return null;
            }
        }
    }

    /// <summary>
    /// Sends one chunk's worth of frames to a session, holding its lock for the whole group so a
    /// format announcement can never be reordered after the samples it describes.
    /// </summary>
    private async Task SendAudioChunkAsync(
        SocketSession session, string id, AudioChunk chunk, IReadOnlyList<byte[]>? packets)
    {
        var frames = new List<(byte[], WebSocketMessageType)>();

        if (session.Codec != LiveAudioCodec.LegacyPcm &&
            session.AnnouncedFormat != (chunk.Channels, chunk.SampleRate))
        {
            frames.Add((FormatMessageBytes(session.Codec, chunk.Channels, chunk.SampleRate), WebSocketMessageType.Text));
            session.AnnouncedFormat = (chunk.Channels, chunk.SampleRate);
        }

        if (session.Codec == LiveAudioCodec.Opus)
        {
            if (packets != null)
            {
                foreach (var packet in packets) frames.Add((packet, WebSocketMessageType.Binary));
            }
        }
        else
        {
            frames.Add((chunk.Pcm, WebSocketMessageType.Binary));
        }

        if (frames.Count > 0) await SendFramesAsync(session, id, frames);
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

    /// <summary>
    /// Sends a group of frames to one session atomically with respect to other senders.
    /// </summary>
    private async Task SendFramesAsync(
        SocketSession session, string id, IReadOnlyList<(byte[] Data, WebSocketMessageType Type)> frames)
    {
        await session.Lock.WaitAsync();
        try
        {
            foreach (var (data, type) in frames)
            {
                if (session.Socket.State != WebSocketState.Open) return;
                await session.Socket.SendAsync(new ArraySegment<byte>(data), type, true, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send audio frames to WebSocket {Id}", id);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    private async Task AnnounceFormatAsync(SocketSession session, string id, int channels, int sampleRate)
    {
        session.AnnouncedFormat = (channels, sampleRate);
        await SendFramesAsync(session, id, new[]
        {
            (FormatMessageBytes(session.Codec, channels, sampleRate), WebSocketMessageType.Text),
        });
    }

    /// <summary>
    /// Builds the <c>AUDIO_FORMAT</c> text frame. Unlike the control socket's state updates, this
    /// travels on the audio socket itself, so it is strictly ordered against the samples it
    /// describes — which is what makes the channel count trustworthy across a scan-mode switch.
    /// </summary>
    private byte[] FormatMessageBytes(LiveAudioCodec codec, int channels, int sampleRate)
    {
        var payload = codec == LiveAudioCodec.Opus
            ? (object)new
            {
                codec = "opus",
                sampleRate,
                channels,
                frameMs = 20,
                bitrate = channels == 1 ? OpusMonoBitrate : OpusStereoBitrate,
            }
            : new { codec = "pcm_s16le", sampleRate, channels };

        var json = JsonSerializer.Serialize(new { type = "AUDIO_FORMAT", payload }, _jsonOptions);
        return Encoding.UTF8.GetBytes(json);
    }
}
