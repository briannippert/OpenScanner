using OpenScanner.Server.Audio;
using Xunit;

namespace OpenScanner.Tests.Audio;

public class AudioMixerTests : IDisposable
{
    private readonly AudioMixer _mixer = new();

    public void Dispose() => _mixer.Dispose();

    private static byte[] Mono(short value, int samples = 960)
    {
        var b = new byte[samples * 2];
        for (int i = 0; i < samples; i++) { b[i * 2] = (byte)(value & 0xFF); b[i * 2 + 1] = (byte)(value >> 8); }
        return b;
    }

    private static (short L, short R) First(AudioChunk? mixed) => First(mixed!.Value);

    private static (short L, short R) First(AudioChunk chunk) =>
        ((short)(chunk.Pcm[0] | (chunk.Pcm[1] << 8)), (short)(chunk.Pcm[2] | (chunk.Pcm[3] << 8)));

    private AudioChunk? MixOnce()
    {
        AudioChunk? got = null;
        void Handler(AudioChunk c) => got = c;
        _mixer.OnAudio += Handler;
        _mixer.Tick();
        _mixer.OnAudio -= Handler;
        return got;
    }

    /// <summary>
    /// A channel outside the layout (a hopper's lock-on) plays at full level on both sides,
    /// identical to the mono stream it replaced.
    /// </summary>
    [Fact]
    public void ChannelOutsideTheLayout_PlaysCentredAtFullLevel()
    {
        _mixer.Open(155.0);
        _mixer.Enqueue(155.0, Mono(1000));

        Assert.Equal((1000, 1000), First(MixOnce()));
    }

    [Fact]
    public void LayoutChannels_ArePannedLowLeftHighRight()
    {
        _mixer.SetLayout(new[] { 460.0, 155.0 });
        _mixer.Open(155.0);
        _mixer.Enqueue(155.0, Mono(1000));
        Assert.Equal((1000, 0), First(MixOnce()));

        _mixer.Open(460.0);
        _mixer.Enqueue(460.0, Mono(1000));
        var (l, r) = First(MixOnce());
        Assert.Equal(0, l);
        Assert.Equal(1000, r);
    }

    [Fact]
    public void SimultaneousChannels_AreSummed()
    {
        _mixer.Open(155.0);
        _mixer.Open(460.0);
        _mixer.Enqueue(155.0, Mono(1000));
        _mixer.Enqueue(460.0, Mono(500));
        Assert.Equal((1500, 1500), First(MixOnce()));
    }

    [Fact]
    public void Silence_EmitsNothing()
    {
        _mixer.Open(155.0);
        Assert.Null(MixOnce());
    }

    [Fact]
    public void ClosedChannel_IsIgnored()
    {
        _mixer.Enqueue(155.0, Mono(1000));
        Assert.Null(MixOnce());

        _mixer.Open(155.0);
        _mixer.Close(155.0);
        _mixer.Enqueue(155.0, Mono(1000));
        Assert.Null(MixOnce());
    }

    [Fact]
    public void Overflow_IsClamped()
    {
        _mixer.Open(155.0);
        _mixer.Open(460.0);
        _mixer.Enqueue(155.0, Mono(30000));
        _mixer.Enqueue(460.0, Mono(30000));
        Assert.Equal((short.MaxValue, short.MaxValue), First(MixOnce()));
    }
}
