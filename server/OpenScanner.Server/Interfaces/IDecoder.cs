using OpenScanner.Server.Models;

namespace OpenScanner.Server.Interfaces;

public interface IDecoder
{
    event Action<byte[]> OnAudio;
    event Action<int?, int?, string?> OnActivity; // src, tgt, tone
    event Action<string> OnMetadata;

    Task StartAsync(Channel channel, CancellationToken token);
    void Stop();
}