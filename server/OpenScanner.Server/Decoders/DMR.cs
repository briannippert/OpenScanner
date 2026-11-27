using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Decoders;

/// <summary>
/// DMR Tier II conventional (repeater and simplex) via dsd-fme. Line formats referenced below
/// are from build_deps/dsd-fme/src (dmr_bs.c, dmr_ms.c, dmr_dburst.c, dmr_flco.c).
/// </summary>
public class DMR : DSDBase
{
    private Channel? _channel;

    // Voice sync:  "Sync: +DMR  [SLOT1]  slot2  | Color Code=01 | VC2"
    // Data sync:   "Sync: +DMR  [slot1]  slot2  | Color Code=01 | IDLE" (lower-case brackets)
    // Call LC:     " SLOT 1 TGT=1234 SRC=5678 Group Emergency Call"
    private static readonly Regex SlotRegex =
        new(@"\[SLOT([12])\]|\bSLOT ([12])\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ColorCodeRegex =
        new(@"Color Code=(\d+)", RegexOptions.Compiled);

    // A voice burst: "| VC1".."| VC6", "| VC1*" (late entry) and "| VC*" (MS/simplex mono).
    private static readonly Regex VoiceBurstRegex =
        new(@"\|\s*VC[\d*]", RegexOptions.Compiled);

    private static readonly Regex TgtSrcRegex =
        new(@"TGT=(\d+)\s+SRC=(\d+)", RegexOptions.Compiled);

    // dmr_flco.c prints "Emergency" after the call type when service-options bit 7 is set.
    private static readonly Regex EmergencyRegex =
        new(@"\bEmergency\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AnsiRegex =
        new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    public DMR(ILogger<DMR> logger) : base(logger)
    {
    }

    public override string GetCommandLine(Channel channel)
    {
        _channel = channel;

        // 48000 Hz: required — dsd-fme -s only accepts 48000 or 96000.
        // -fs: DMR TDMA BS and MS Simplex.
        // -ma: auto-select modulation optimizer — more robust than forcing -mg (GFSK).
        // -V: which slot(s) to synthesize voice for — the configured slot, or both (3).
        // Do NOT pass the talkgroup as -I (TG hold). TG hold mutes a slot until its talkgroup
        // is known (dsd_audio2.c compares against state->lasttg, which starts at 0), and the
        // scanner locks on after a call has started, missing the voice header that carries
        // it. Every burst is then muted until an embedded LC decodes cleanly, which on a
        // marginal (or pulsed simplex) signal can be the whole call. The talkgroup filter is
        // applied in ParseMetadata instead.
        //
        // Audio: in -fs mode dsd-fme always writes 8 kHz interleaved STEREO (L=slot 1,
        // R=slot 2). When only one slot is enabled, or only one slot carries voice, it copies
        // that slot to both channels, so ffmpeg's -ac 2 -> -ac 1 downmix yields full level.
        // There was once a volume=2.0 here on the theory that R was silent; it isn't, and the
        // doubling clipped every call.
        int captureRate = 48000;
        int outputRate = 48000;
        int dsdOutputRate = 8000;
        string rtlMode = "fm";

        int voiceSlots = channel.DmrSlot is 1 or 2 ? channel.DmrSlot.Value : 3;
        string dsdArgs = $"-fs -ma -V {voiceSlots}";

        // -l 0: no squelch, for the same reason as P25 — dsd-fme needs an unbroken sample
        // stream to hold TDMA sync. This was already implicit (rtl_fm defaults to 0); it is
        // now explicit so it cannot be "tidied" back into a squelched pipeline.
        string source = InputSource ?? $"{PlatformTools.RtlFm} -f {channel.Frequency}M -s {captureRate} -r {outputRate} {Tuning.RtlFmArgs()} -l 0 -M {rtlMode} -";

        return $"{PlatformTools.Stdbuf("-o0")}{source} | {PlatformTools.Stdbuf("-i0 -o0")}{PlatformTools.DsdFme} {dsdArgs} -i - -o - -s {outputRate} | {PlatformTools.Stdbuf("-o0")}{PlatformTools.Ffmpeg} -f s16le -ar {dsdOutputRate} -ac 2 -probesize 32 -analyzeduration 0 -i - -f s16le -ar 48000 -ac 1 -fflags nobuffer -flags low_delay -flush_packets 1 - -loglevel quiet";
    }

    /// <summary>Test hook: parse one dsd-fme stderr line.</summary>
    internal void ParseLine(string line) => ParseMetadata(line);

    /// <summary>Test hook: bind the channel without building a command line.</summary>
    internal void SetChannel(Channel channel) => _channel = channel;

    // Per-slot state carried between lines: voice-burst lines carry the color code but not
    // the talkgroup, call-LC lines the reverse, so each filter has to remember what the
    // other kind of line said. Index 0 = no slot marker, 1 and 2 = TDMA slots.
    private readonly int?[] _slotColorCode = new int?[3];
    private readonly int?[] _slotTalkgroup = new int?[3];

    protected override void ParseMetadata(string line)
    {
        line = AnsiRegex.Replace(line, "");

        var slotMatch = SlotRegex.Match(line);
        // MS/direct-mode (simplex) lines carry no slot marker, but dsd-fme forces simplex to
        // slot 1 (dmr_ms.c) and prints its call LC as "SLOT 1", so file them together.
        int slot = slotMatch.Success
            ? int.Parse(slotMatch.Groups[1].Success ? slotMatch.Groups[1].Value : slotMatch.Groups[2].Value)
            : line.Contains("MS/DM") ? 1 : 0;

        // Track color code from every sync line (voice or data), so a co-channel repeater's
        // call LC — which carries no color code — is rejected too. "XX" (undecoded) is ignored.
        var ccMatch = ColorCodeRegex.Match(line);
        if (ccMatch.Success && int.TryParse(ccMatch.Groups[1].Value, out var cc))
            _slotColorCode[slot] = cc;

        // An idle slot has no call, so forget its talkgroup — otherwise a late-entry voice
        // burst of the next call would inherit the previous call's talkgroup.
        if (line.Contains("IDLE")) _slotTalkgroup[slot] = null;

        // Only voice counts as activity. Idle bursts, CSBKs, data headers and sync errors are
        // sent by a repeater in hang time or beaconing; treating them as activity kept the
        // channel "busy" and produced recordings of dead air.
        bool isVoiceBurst = line.Contains("Sync:") && VoiceBurstRegex.IsMatch(line);
        var call = TgtSrcRegex.Match(line);
        bool isCallLc = call.Success;

        if (!isVoiceBurst && !isCallLc) return;

        // Record the call's talkgroup and source before any filter can return early, so a call
        // LC that is itself held back (e.g. color code not yet known) still tags the voice
        // bursts that follow it.
        int? src = null;
        int? tgt = null;
        if (isCallLc)
        {
            if (int.TryParse(call.Groups[1].Value, out var t)) tgt = t;
            if (int.TryParse(call.Groups[2].Value, out var s)) src = s;
            _slotTalkgroup[slot] = tgt;
        }

        // Slot filter. Lines with no slot (MS/simplex mono) pass.
        if (slot != 0 && _channel?.DmrSlot is int wantSlot && slot != wantSlot) return;

        // With a color code configured, wait until the slot's is known: the first call LC of a
        // simplex call is printed while dsd-fme still shows "Color Code=XX", and letting it
        // through would start a recording for a co-channel user on a different color code.
        if (_channel?.DmrColorCode is int wantCc && _slotColorCode[slot] != wantCc) return;

        // Talkgroup filter: voice bursts inherit the slot's last call LC talkgroup.
        int? lineTg = tgt ?? _slotTalkgroup[slot];
        if (_channel?.DmrTalkgroup is int wantTg && lineTg.HasValue && lineTg != wantTg)
            return;

        string? tone = isCallLc && EmergencyRegex.IsMatch(line) ? "EMRG" : null;
        RaiseActivity(src, tgt, tone);
    }
}
