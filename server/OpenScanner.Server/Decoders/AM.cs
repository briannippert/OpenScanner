using Microsoft.Extensions.Logging;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Decoders;

public class AM : DSDBase
{
    public AM(ILogger<AM> logger) : base(logger)
    {
    }

    public override string GetCommandLine(Channel channel)
    {
        int outputRate = 48000;
        int captureRate = 8000;

        // Simplified pipeline: No tee/dsd-fme for now to guarantee audio flow.
        // rtl_fm outputs s16le at 8k rate. 
        // ffmpeg resamples to 48k and applies bandpass/volume.
        // -l 0: No squelch once started (the scanner already detected a carrier).
        return $"stdbuf -o0 rtl_fm -f {channel.Frequency}M -M am -s {captureRate} -g 35 -p 0 -l 0 - | stdbuf -o0 /usr/bin/ffmpeg -f s16le -ar {captureRate} -ac 1 -i - -af 'highpass=f=300,lowpass=f=4000,volume=4.0' -f s16le -ar {outputRate} -ac 1 -fflags nobuffer -flags low_delay -flush_packets 1 - -loglevel quiet";
    }

    protected override Task OnStarted(CancellationToken token)
    {
        // For analog modes, we assume activity is present since the scanner locked on.
        // This starts the recording and keep-alive mechanisms immediately.
        RaiseActivity(null, null, "ANALOG");
        return Task.CompletedTask;
    }
}