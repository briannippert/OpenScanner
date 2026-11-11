using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenScanner.Server.Audio;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests.Audio;

/// <summary>
/// Covers the <c>/ws/audio</c> wire protocol, which had no test coverage before Opus negotiation.
/// </summary>
public class WebSocketBroadcasterAudioTests
{
    /// <summary>A WebSocket that records everything sent to it and blocks in ReceiveAsync.</summary>
    private sealed class FakeWebSocket : WebSocket
    {
        private readonly TaskCompletionSource<WebSocketReceiveResult> _receive = new();
        private volatile WebSocketState _state = WebSocketState.Open;
        public List<(byte[] Data, WebSocketMessageType Type)> Sent { get; } = new();

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override void Dispose() { }

        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken t) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken t) => Task.CompletedTask;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
            => _receive.Task;

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
        {
            lock (Sent) Sent.Add((buffer.ToArray(), type));
            return Task.CompletedTask;
        }

        public void EndSession()
        {
            _state = WebSocketState.Closed;
            _receive.TrySetResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        }

        public IReadOnlyList<(byte[] Data, WebSocketMessageType Type)> Snapshot()
        {
            lock (Sent) return Sent.ToList();
        }
    }

    private static (WebSocketBroadcaster Broadcaster, Mock<IRadioSource> Radio) Create(
        byte[][]? preRoll = null)
    {
        var radio = new Mock<IRadioSource>();
        radio.Setup(r => r.GetPreRollBuffer()).Returns(preRoll ?? Array.Empty<byte[]>());
        var broadcaster = new WebSocketBroadcaster(radio.Object, NullLogger<WebSocketBroadcaster>.Instance);
        return (broadcaster, radio);
    }

    /// <summary>Connects a session and waits until the broadcaster has registered it.</summary>
    private static async Task<(FakeWebSocket Socket, Task Session)> Connect(
        WebSocketBroadcaster broadcaster, LiveAudioCodec codec)
    {
        var socket = new FakeWebSocket();
        var session = broadcaster.HandleAudioConnection(socket, codec);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (broadcaster.AudioClientCount == 0 && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.Equal(1, broadcaster.AudioClientCount);
        return (socket, session);
    }

    private static async Task Drain(FakeWebSocket socket, Task session)
    {
        socket.EndSession();
        await session.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void RaiseAudio(Mock<IRadioSource> radio, AudioChunk chunk) =>
        radio.Raise(r => r.OnAudio += null, chunk);

    private static byte[] Pcm(int bytes) => Enumerable.Range(0, bytes).Select(i => (byte)(i % 251)).ToArray();

    private static JsonElement ParseText((byte[] Data, WebSocketMessageType Type) frame)
    {
        Assert.Equal(WebSocketMessageType.Text, frame.Type);
        return JsonDocument.Parse(Encoding.UTF8.GetString(frame.Data)).RootElement;
    }

    /// <summary>
    /// The compatibility guarantee, asserted mechanically: a client that sends no ?codec= gets the
    /// exact bytes it got before this feature existed, and zero text frames.
    /// </summary>
    [Fact]
    public async Task LegacySession_ReceivesByteIdenticalPcmAndNoTextFrames()
    {
        var preRoll = new[] { Pcm(64), Pcm(96) };
        var (broadcaster, radio) = Create(preRoll);
        var (socket, session) = await Connect(broadcaster, LiveAudioCodec.LegacyPcm);

        var live = Pcm(4096);
        RaiseAudio(radio, AudioChunk.Mono48k(live));
        await Task.Delay(50);
        await Drain(socket, session);

        var frames = socket.Snapshot();
        Assert.All(frames, f => Assert.Equal(WebSocketMessageType.Binary, f.Type));
        Assert.Equal(new[] { preRoll[0], preRoll[1], live }, frames.Select(f => f.Data));
    }

    [Fact]
    public async Task NegotiatedPcmSession_GetsAudioFormatBeforeSamples()
    {
        var (broadcaster, radio) = Create();
        var (socket, session) = await Connect(broadcaster, LiveAudioCodec.Pcm);

        var live = Pcm(4096);
        RaiseAudio(radio, AudioChunk.Mono48k(live));
        await Task.Delay(50);
        await Drain(socket, session);

        var frames = socket.Snapshot();
        var format = ParseText(frames[0]);
        Assert.Equal("AUDIO_FORMAT", format.GetProperty("type").GetString());
        Assert.Equal("pcm_s16le", format.GetProperty("payload").GetProperty("codec").GetString());
        Assert.Equal(1, format.GetProperty("payload").GetProperty("channels").GetInt32());
        Assert.Equal(48000, format.GetProperty("payload").GetProperty("sampleRate").GetInt32());

        // Samples themselves are untouched — only the metadata is new.
        Assert.Equal(live, frames.Last().Data);
    }

    [Fact]
    public async Task OpusSession_GetsOpusFormatThenCompressedBinaryFrames()
    {
        var (broadcaster, radio) = Create();
        var (socket, session) = await Connect(broadcaster, LiveAudioCodec.Opus);

        // 10 chunks of 4096 bytes = 20480 samples = 21 complete 960-sample frames.
        for (int i = 0; i < 10; i++) RaiseAudio(radio, AudioChunk.Mono48k(Pcm(4096)));
        await Task.Delay(100);
        await Drain(socket, session);

        var frames = socket.Snapshot();
        var format = ParseText(frames[0]);
        Assert.Equal("opus", format.GetProperty("payload").GetProperty("codec").GetString());
        Assert.Equal(20, format.GetProperty("payload").GetProperty("frameMs").GetInt32());

        var packets = frames.Skip(1).ToList();
        Assert.All(packets, p => Assert.Equal(WebSocketMessageType.Binary, p.Type));
        Assert.Equal(21, packets.Count);

        // The whole point: 40960 bytes of PCM in, a fraction of that out.
        int encoded = packets.Sum(p => p.Data.Length);
        Assert.True(encoded < 40960 / 10, $"expected >10x compression, got {40960.0 / encoded:F1}x");
    }

    /// <summary>
    /// The mono/stereo race fix: switching scan modes re-announces the format on the audio socket,
    /// strictly before the first frame that uses it.
    /// </summary>
    [Fact]
    public async Task ChannelCountChange_ReAnnouncesFormatBeforeTheNewSamples()
    {
        var (broadcaster, radio) = Create();
        var (socket, session) = await Connect(broadcaster, LiveAudioCodec.Pcm);

        RaiseAudio(radio, AudioChunk.Mono48k(Pcm(3840)));
        await Task.Delay(30);
        int beforeSwitch = socket.Snapshot().Count;
        RaiseAudio(radio, AudioChunk.Stereo48k(Pcm(3840)));
        await Task.Delay(30);
        await Drain(socket, session);

        var frames = socket.Snapshot();
        var announcement = ParseText(frames[beforeSwitch]);
        Assert.Equal("AUDIO_FORMAT", announcement.GetProperty("type").GetString());
        Assert.Equal(2, announcement.GetProperty("payload").GetProperty("channels").GetInt32());
        Assert.Equal(WebSocketMessageType.Binary, frames[beforeSwitch + 1].Type);
    }

    [Fact]
    public async Task OpusSession_ReceivesThePreRollReEncoded()
    {
        // 5 chunks of 4096 bytes = 10240 samples = 10 frames plus a padded tail.
        var preRoll = Enumerable.Range(0, 5).Select(_ => Pcm(4096)).ToArray();
        var (broadcaster, radio) = Create(preRoll);
        var (socket, session) = await Connect(broadcaster, LiveAudioCodec.Opus);
        await Drain(socket, session);

        var frames = socket.Snapshot();
        Assert.Equal(WebSocketMessageType.Text, frames[0].Type);
        var packets = frames.Skip(1).ToList();
        Assert.Equal(11, packets.Count);
        Assert.All(packets, p => Assert.Equal(WebSocketMessageType.Binary, p.Type));
    }

    /// <summary>
    /// PCM and Opus listeners at the same time each get their own representation of one chunk.
    /// </summary>
    [Fact]
    public async Task MixedSessions_EachGetTheirNegotiatedFormat()
    {
        var (broadcaster, radio) = Create();
        var pcmSocket = new FakeWebSocket();
        var pcmSession = broadcaster.HandleAudioConnection(pcmSocket, LiveAudioCodec.LegacyPcm);
        var opusSocket = new FakeWebSocket();
        var opusSession = broadcaster.HandleAudioConnection(opusSocket, LiveAudioCodec.Opus);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (broadcaster.AudioClientCount < 2 && DateTime.UtcNow < deadline) await Task.Delay(5);

        var live = Pcm(3840);
        RaiseAudio(radio, AudioChunk.Mono48k(live));
        await Task.Delay(50);
        await Drain(pcmSocket, pcmSession);
        await Drain(opusSocket, opusSession);

        Assert.Equal(new[] { live }, pcmSocket.Snapshot().Select(f => f.Data));

        var opusFrames = opusSocket.Snapshot();
        Assert.Equal(WebSocketMessageType.Text, opusFrames[0].Type);
        // 3840 bytes of mono is 1920 samples: two 20 ms frames.
        Assert.Equal(3, opusFrames.Count);
        Assert.All(opusFrames.Skip(1), f =>
        {
            Assert.Equal(WebSocketMessageType.Binary, f.Type);
            Assert.True(f.Data.Length < 200, $"packet was {f.Data.Length} bytes");
        });
    }
}
