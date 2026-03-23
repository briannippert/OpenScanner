using Microsoft.Extensions.Logging;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Decoders;

public class DMR : DSDBase
{
    private Channel? _channel;

    public DMR(ILogger<DMR> logger) : base(logger)
    {
    }

    public override string GetCommandLine(Channel channel)
    {
        _channel = channel;

        // 24000 Hz: well-tested rate for DMR via dsd-fme stdin pipe from rtl_fm.
        // -fs: DMR TDMA BS and MS Simplex — correct flag for direct DMR channels.
        // -V 3: explicit TDMA voice synthesis on both slots (default, but be explicit).
        int captureRate = 24000;
        int outputRate = 24000;
        int dsdOutputRate = 8000;
        string rtlMode = "fm";
        string dsdArgs = "-fs -V 3"; // DMR TDMA Simplex, both slots

        string source = InputSource ?? $"rtl_fm -f {channel.Frequency}M -s {captureRate} -r {outputRate} -g 45 -p 0 -M {rtlMode} -";

        return $"stdbuf -o0 {source} | stdbuf -i0 -o0 /usr/local/bin/dsd-fme {dsdArgs} -i - -o - -s {outputRate} | stdbuf -o0 /usr/bin/ffmpeg -f s16le -ar {dsdOutputRate} -ac 1 -probesize 32 -analyzeduration 0 -i - -f s16le -ar 48000 -ac 1 -fflags nobuffer -flags low_delay -flush_packets 1 - -loglevel quiet";
    }

    protected override void ParseMetadata(string line)
    {
        // Detect which DMR slot this line belongs to
        int? lineSlot = null;
        if (line.Contains("Slot 1")) lineSlot = 1;
        else if (line.Contains("Slot 2")) lineSlot = 2;

        // Filter by configured slot — skip lines from the wrong slot
        if (_channel?.DmrSlot.HasValue == true && lineSlot.HasValue && lineSlot != _channel.DmrSlot)
            return;

        // Broad activity detection: includes lock/sync lines so HandleActivity fires
        // early and the session timeout gets reset while dsd-fme is still syncing.
        bool isActivity =
            line.Contains("Voice") ||
            line.Contains("Slot 1") || line.Contains("Slot 2") ||
            line.Contains("CACH") ||
            line.Contains("LC:") ||       // Link Control — present during sync lock
            line.Contains("MFID:") ||     // Manufacturer ID — present in voice header
            line.Contains("Sync:") ||     // dsd-fme sync lock line
            line.Contains("SYNC") ||      // alternate sync format
            (line.Contains("DMR") && !line.Contains("dsd-fme")); // avoid matching our own log lines

        if (!isActivity) return;

        int? src = null;
        int? tgt = null;

        if (line.Contains("Src:"))
        {
            var parts = line.Split("Src:");
            if (parts.Length > 1 && int.TryParse(parts[1].Trim().Split(' ')[0], out var s)) src = s;
        }
        else if (line.Contains("Source:"))
        {
            var parts = line.Split("Source:");
            if (parts.Length > 1 && int.TryParse(parts[1].Trim().Split(' ')[0], out var s)) src = s;
        }

        if (line.Contains("Dst:"))
        {
            var parts = line.Split("Dst:");
            if (parts.Length > 1 && int.TryParse(parts[1].Trim().Split(' ')[0], out var t)) tgt = t;
        }
        else if (line.Contains("Tgt:"))
        {
            var parts = line.Split("Tgt:");
            if (parts.Length > 1 && int.TryParse(parts[1].Trim().Split(' ')[0], out var t)) tgt = t;
        }
        else if (line.Contains("Target:"))
        {
            var parts = line.Split("Target:");
            if (parts.Length > 1 && int.TryParse(parts[1].Trim().Split(' ')[0], out var t)) tgt = t;
        }

        // Filter by configured talkgroup — skip if talkgroup doesn't match
        if (_channel?.DmrTalkgroup.HasValue == true && tgt.HasValue && tgt != _channel.DmrTalkgroup)
            return;

        RaiseActivity(src, tgt, null);
    }
}
