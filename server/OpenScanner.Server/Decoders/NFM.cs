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
        
        // NFM with MDC1200 and analog signaling detection via dsd-fme
        // dsd-fme in analog monitor mode (-fA) detects MDC1200, CTCSS, and other signaling
        // Metadata output goes to stderr for parsing, audio to stdout
        return $"stdbuf -o0 rtl_fm -f {channel.Frequency}M -M fm -s {captureRate} -r {outputRate} -g 42 -p 0 -l 50 -t 30 - | stdbuf -i0 -o0 /usr/local/bin/dsd-fme -fA -i - -s {outputRate} -o -";
    }

    protected override Task OnStarted(CancellationToken token)
    {
        // For analog modes, we assume activity is present since the scanner locked on.
        // This starts the recording and keep-alive mechanisms immediately.
        RaiseActivity(null, null, "ANALOG");
        return Task.CompletedTask;
    }
}