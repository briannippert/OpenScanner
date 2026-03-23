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

        // 48000 Hz: required — dsd-fme -s only accepts 48000 or 96000.
        // -fs: DMR TDMA BS and MS Simplex.
        // -ma: auto-select modulation optimizer — more robust than forcing -mg (GFSK).
        // -V 1: synthesize voice for slot 1 only. -V 3 (both slots) doubles the PCM
        //       output data on a single-slot call, making audio play at half speed.
        //
        // IMPORTANT: -fs hardcodes pulse_digi_out_channels=2 in dsd-fme source (dmr_bs.c).
        // dsd-fme always writes STEREO (interleaved L=slot1, R=slot2) to stdout when
        // using any DMR simplex mode, regardless of -V. With -V 1, R channel is silence.
        // ffmpeg must read with -ac 2; volume=2.0 compensates for the L+R downmix
        // halving the volume (out = (L+0)/2 = L/2 without compensation).
        int captureRate = 48000;
        int outputRate = 48000;
        int dsdOutputRate = 8000;
        string rtlMode = "fm";
        string dsdArgs = "-fs -ma -V 1"; // DMR TDMA Simplex, auto modulation, slot 1

        string source = InputSource ?? $"rtl_fm -f {channel.Frequency}M -s {captureRate} -r {outputRate} -g 45 -p 0 -M {rtlMode} -";

        return $"stdbuf -o0 {source} | stdbuf -i0 -o0 /usr/local/bin/dsd-fme {dsdArgs} -i - -o - -s {outputRate} | stdbuf -o0 /usr/bin/ffmpeg -f s16le -ar {dsdOutputRate} -ac 2 -probesize 32 -analyzeduration 0 -i - -af volume=2.0 -f s16le -ar 48000 -ac 1 -fflags nobuffer -flags low_delay -flush_packets 1 - -loglevel quiet";
    }

    protected override void ParseMetadata(string line)
    {
        // Detect which DMR slot this line belongs to.
        // dsd-fme emits "SLOT 1" / "SLOT 2" (all caps).
        int? lineSlot = null;
        if (line.Contains("SLOT 1")) lineSlot = 1;
        else if (line.Contains("SLOT 2")) lineSlot = 2;

        // Filter by configured slot — skip lines from the wrong slot
        if (_channel?.DmrSlot.HasValue == true && lineSlot.HasValue && lineSlot != _channel.DmrSlot)
            return;

        // Broad activity detection: includes lock/sync lines so HandleActivity fires
        // early and the session timeout gets reset while dsd-fme is still syncing.
        bool isActivity =
            line.Contains("Voice") ||
            line.Contains("SLOT 1") || line.Contains("SLOT 2") ||
            line.Contains("CACH") ||
            line.Contains("LC:") ||
            line.Contains("MFID:") ||
            line.Contains("Sync:") ||
            line.Contains("Group Call") ||
            line.Contains("Private Call") ||
            (line.Contains("DMR") && !line.Contains("dsd-fme"));

        if (!isActivity) return;

        int? src = null;
        int? tgt = null;

        // dsd-fme DMR output: "SLOT 1 TGT=763901 SRC=12345678 Group Call"
        if (line.Contains("SRC="))
        {
            var parts = line.Split("SRC=");
            if (parts.Length > 1 && int.TryParse(parts[1].Trim().Split(' ')[0], out var s)) src = s;
        }
        else if (line.Contains("Src:"))
        {
            var parts = line.Split("Src:");
            if (parts.Length > 1 && int.TryParse(parts[1].Trim().Split(' ')[0], out var s)) src = s;
        }
        else if (line.Contains("Source:"))
        {
            var parts = line.Split("Source:");
            if (parts.Length > 1 && int.TryParse(parts[1].Trim().Split(' ')[0], out var s)) src = s;
        }

        if (line.Contains("TGT="))
        {
            var parts = line.Split("TGT=");
            if (parts.Length > 1 && int.TryParse(parts[1].Trim().Split(' ')[0], out var t)) tgt = t;
        }
        else if (line.Contains("Dst:"))
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
