using OpenScanner.Server.Audio;
using Xunit;

namespace OpenScanner.Tests.Audio;

public class AudioNegotiationTests
{
    /// <summary>
    /// The compatibility hinge: a client that never heard of codec negotiation sends no parameter
    /// and must land in legacy mode, where it gets raw PCM and no text frames.
    /// </summary>
    [Fact]
    public void MissingParameter_IsLegacyPcm()
    {
        Assert.Equal(LiveAudioCodec.LegacyPcm, AudioNegotiation.Negotiate(null, opusAvailable: true));
    }

    [Theory]
    [InlineData("opus")]
    [InlineData("opus,pcm")]
    [InlineData(" OPUS , pcm ")]
    public void OpusPreferred_SelectsOpus(string param)
    {
        Assert.Equal(LiveAudioCodec.Opus, AudioNegotiation.Negotiate(param, opusAvailable: true));
    }

    [Theory]
    [InlineData("pcm")]
    [InlineData("pcm_s16le")]
    [InlineData("pcm,opus")]
    public void PcmPreferred_SelectsPcm(string param)
    {
        Assert.Equal(LiveAudioCodec.Pcm, AudioNegotiation.Negotiate(param, opusAvailable: true));
    }

    [Fact]
    public void OpusRequested_ButUnavailable_FallsBackToNegotiatedPcm()
    {
        Assert.Equal(LiveAudioCodec.Pcm, AudioNegotiation.Negotiate("opus", opusAvailable: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("flac")]
    [InlineData("mp3,aac")]
    public void UnknownCodecs_FallBackToNegotiatedPcm(string param)
    {
        // Not legacy: the client did send the parameter, so it speaks the negotiated protocol.
        Assert.Equal(LiveAudioCodec.Pcm, AudioNegotiation.Negotiate(param, opusAvailable: true));
    }
}
