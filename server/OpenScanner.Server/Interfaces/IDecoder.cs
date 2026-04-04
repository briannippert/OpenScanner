using OpenScanner.Server.Models;

namespace OpenScanner.Server.Interfaces;

public interface IDecoder
{
    event Action<byte[]> OnAudio;
    event Action<int?, int?, string?> OnActivity; // src, tgt, tone
    event Action<string> OnMetadata;

    string? InputSource { get; set; }

    /// <summary>
    /// Whether this decoder supports being fed audio via <see cref="FeedInput"/>.
    /// True when InputSource is set (stdin-based feeding mode).
    /// </summary>
    bool CanFeedInput { get; }

    /// <summary>
    /// Write FM-demodulated audio data to the decoder's stdin.
    /// Only works when <see cref="CanFeedInput"/> is true and the decoder is running.
    /// </summary>
    void FeedInput(byte[] data, int offset, int count);

    Task StartAsync(Channel channel, CancellationToken token);
    void Stop();
}