using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Decoders;

public class P25 : DSDBase
{
    // Matches: "Group Voice Channel User - Group 12345 Source 67890"
    private static readonly Regex GroupCallRegex =
        new(@"Group\s+(\d+)\s+Source\s+(\d+)", RegexOptions.Compiled);

    // Matches: "Unit to Unit Voice Channel User - Target 12345 Source 67890"
    private static readonly Regex UnitCallRegex =
        new(@"Target\s+(\d+)\s+Source\s+(\d+)", RegexOptions.Compiled);

    // Matches: "TGT: 12345; SRC: 67890;" (extended unit-to-unit)
    private static readonly Regex ExtendedCallRegex =
        new(@"TGT:\s*(\d+).*?SRC:\s*(\d+)", RegexOptions.Compiled);

    public P25(ILogger<P25> logger) : base(logger)
    {
    }

    public override string GetCommandLine(Channel channel)
    {
        int captureRate = 48000;
        int outputRate = 48000;
        int dsdOutputRate = 8000;
        string rtlMode = "fm";
        string dsdArgs = "-f1"; // P25 Phase 1

        string source = InputSource ?? $"rtl_fm -f {channel.Frequency}M -s {captureRate} -r {outputRate} -g 42 -p 0 -l 50 -t 30 -M {rtlMode} -";

        return $"stdbuf -o0 {source} | stdbuf -i0 -o0 /usr/local/bin/dsd-fme {dsdArgs} -i - -o - -s {outputRate} | stdbuf -o0 /usr/bin/ffmpeg -f s16le -ar {dsdOutputRate} -ac 1 -probesize 32 -analyzeduration 0 -i - -f s16le -ar {outputRate} -ac 1 -fflags nobuffer -flags low_delay -flush_packets 1 - -loglevel quiet";
    }

    protected override void ParseMetadata(string line)
    {
        // P25 activity detection — avoid TSBK (control channel data bursts)
        bool isActivity =
            line.Contains("LDU") ||          // P25 Voice Frame (LDU1/LDU2)
            line.Contains("HDU") ||          // Header Data Unit — start of call
            line.Contains("TDU") ||          // Terminator — end of call
            line.Contains("Voice") ||
            line.Contains("Group Voice") ||
            line.Contains("Unit to Unit") ||
            line.Contains("TGT:") ||         // Extended unit-to-unit LCW
            (line.Contains("P25") && !line.Contains("TSBK"));

        if (!isActivity) return;

        int? src = null;
        int? tgt = null;

        // "Group Voice Channel User - Group 12345 Source 67890"
        var m = GroupCallRegex.Match(line);
        if (m.Success)
        {
            if (int.TryParse(m.Groups[1].Value, out var g)) tgt = g;
            if (int.TryParse(m.Groups[2].Value, out var s)) src = s;
        }
        else
        {
            // "Unit to Unit Voice Channel User - Target 12345 Source 67890"
            m = UnitCallRegex.Match(line);
            if (m.Success)
            {
                if (int.TryParse(m.Groups[1].Value, out var t)) tgt = t;
                if (int.TryParse(m.Groups[2].Value, out var s)) src = s;
            }
            else
            {
                // "TGT: 12345; SRC: 67890;"
                m = ExtendedCallRegex.Match(line);
                if (m.Success)
                {
                    if (int.TryParse(m.Groups[1].Value, out var t)) tgt = t;
                    if (int.TryParse(m.Groups[2].Value, out var s)) src = s;
                }
            }
        }

        string? tone = line.Contains("Emergency") ? "EMRG" : null;
        RaiseActivity(src, tgt, tone);
    }
}