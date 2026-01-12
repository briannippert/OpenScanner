using Microsoft.Extensions.Logging;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Decoders;

public class WFM : DSDBase
{
    // I'm not sure if any public safety actually uses WFM but it makes it easier for me to test with local radio stations
    public WFM(ILogger<WFM> logger) : base(logger)
    {
    }

    public override string GetCommandLine(Channel channel)
    {
        int captureRate = 170000;
        int outputRate = 48000;
        string rtlMode = "wbfm";
        // WFM: Bypass dsd-fme
        return $"stdbuf -o0 rtl_fm -f {channel.Frequency}M -s {captureRate} -r {outputRate} -g 45 -p 0 -M {rtlMode} -";
    }

    protected override Task OnStarted(CancellationToken token)
    {
        _ = Task.Run(async () => 
        {
            try {
                while (!token.IsCancellationRequested) {
                     RaiseActivity(null, null, null);
                     await Task.Delay(2000, token);
                }
            } 
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                 _logger.LogDebug(ex, "WFM Keep-Alive error");
            }
        }, token);
        return Task.CompletedTask;
    }
}