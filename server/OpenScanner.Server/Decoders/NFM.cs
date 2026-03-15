using Microsoft.Extensions.Logging;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Decoders;

public class NFM : DSDBase
{
    public NFM(ILogger<NFM> logger) : base(logger)
    {
    }

    public override string GetCommandLine(Channel channel)
    {
        int captureRate = 48000;
        int outputRate = 48000;
        
        // NFM with MDC1200 detection using parallel decoder
        // Uses tee to split audio: one stream for audio, another for MDC1200 decoder
        // MDC decoder outputs unit IDs to stderr for real-time display and logging
        string decoderScript = "/home/brian/Documents/OpenScanner/scripts/mdc1200_decoder.py";
        return $"stdbuf -o0 rtl_fm -f {channel.Frequency}M -M fm -s {captureRate} -r {outputRate} -g 42 -p 0 -l 50 -t 30 - | " +
               $"stdbuf -o0 tee >(stdbuf -i0 -o0 python3 {decoderScript}) | " +
               $"stdbuf -o0 cat";
    }

    protected override Task OnStarted(CancellationToken token)
    {
        // For analog modes, we assume activity is present since the scanner locked on.
        // This starts the recording and keep-alive mechanisms immediately.
        RaiseActivity(null, null, "ANALOG");
        return Task.CompletedTask;
    }
}