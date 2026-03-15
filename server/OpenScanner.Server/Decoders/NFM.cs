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
        int outputRate = 48000;

        // Simplified pipeline for lower latency:
        // Use rtl_fm's internal resampling (-r 48k) to avoid ffmpeg overhead.
        // ffmpeg now only handles voice filtering and volume boost.
        // -l 0: Disable software squelch once carrier is confirmed by scanner.
        return $"stdbuf -o0 rtl_fm -f {channel.Frequency}M -M fm -s 12k -r {outputRate} -g 35 -p 0 -l 0 - | /usr/bin/ffmpeg -f s16le -ar {outputRate} -ac 1 -i - -af 'highpass=f=300,lowpass=f=4000,volume=4.0' -f s16le -ar {outputRate} -ac 1 -fflags nobuffer -flags low_delay -flush_packets 1 - -loglevel quiet";
    }

    protected override Task OnStarted(CancellationToken token)
    {
        // For analog modes, we assume activity is present since the scanner locked on.
        // This starts the recording and keep-alive mechanisms immediately.
        RaiseActivity(null, null, "ANALOG");
        return Task.CompletedTask;
    }
}