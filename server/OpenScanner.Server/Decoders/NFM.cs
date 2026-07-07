using Microsoft.Extensions.Logging;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Decoders;

public class NFM : DSDBase
{
    public NFM(ILogger<NFM> logger) : base(logger)
    {
    }

    // rtl_fm analog FM output is low-level; apply software gain to compensate.
    protected override float AudioGain => 3.0f;

    public override string GetCommandLine(Channel channel)
    {
        int captureRate = 48000;
        int outputRate = 48000;
        
        // NFM decoder with optimized squelch and gain settings
        // Simplified pipeline: no parallel MDC processing to avoid audio artifacts
        return $"{PlatformTools.Stdbuf("-o0")}{PlatformTools.RtlFm} -f {channel.Frequency}M -M fm -s {captureRate} -r {outputRate} -g 42 -p 0 -l 50 -t 30 -";
    }

    protected override Task OnStarted(CancellationToken token)
    {
        // For analog modes, we assume activity is present since the scanner locked on.
        // This starts the recording and keep-alive mechanisms immediately.
        RaiseActivity(null, null, null);
        return Task.CompletedTask;
    }
}