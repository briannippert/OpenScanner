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
        // NFM with moderate squelch and increased gain for better signal handling
        return $"stdbuf -o0 rtl_fm -f {channel.Frequency}M -M fm -s 48000 -r 48000 -g 42 -p 0 -l 50 -t 30 -";
    }

    protected override Task OnStarted(CancellationToken token)
    {
        // For analog modes, we assume activity is present since the scanner locked on.
        // This starts the recording and keep-alive mechanisms immediately.
        RaiseActivity(null, null, "ANALOG");
        return Task.CompletedTask;
    }
}