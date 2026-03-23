using Microsoft.Extensions.Logging;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Decoders;

public class DMR : DSDBase
{
    public DMR(ILogger<DMR> logger) : base(logger)
    {
    }

    public override string GetCommandLine(Channel channel)
    {
        int captureRate = 48000;
        int outputRate = 48000;
        int dsdOutputRate = 8000;
        string rtlMode = "fm";
        string dsdArgs = "-fd"; // DMR

        string source = InputSource ?? $"rtl_fm -f {channel.Frequency}M -s {captureRate} -r {outputRate} -g 45 -p 0 -M {rtlMode} -";

        return $"stdbuf -o0 {source} | stdbuf -i0 -o0 /usr/local/bin/dsd-fme {dsdArgs} -i - -o - -s {outputRate} | stdbuf -o0 /usr/bin/ffmpeg -f s16le -ar {dsdOutputRate} -ac 1 -probesize 32 -analyzeduration 0 -i - -f s16le -ar {outputRate} -ac 1 -fflags nobuffer -flags low_delay -flush_packets 1 - -loglevel quiet";
    }
}
