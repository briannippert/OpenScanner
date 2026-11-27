# Off-air test captures

Real transmissions recorded with an RTL-SDR (R820T) on 2026-11-27. They are used by
`OpenScanner.Tests/Decoders/OffAirDecodeTests.cs`.

| File | Signal |
|---|---|
| `p25_nac113_tg1.iq.u8` | P25 Phase 1 conventional, NAC 113 (0x071), TG 1, source 12345678. One full call: HDU, 10 LDU1/LDU2 pairs, TDU. 5.5 s. |
| `dmr_simplex_ts1_cc9_tg763901.iq.u8` | DMR simplex (MS/direct mode), TS1, CC 9, TG 763901, source 12345678. One full call: VLC header, 12 voice superframes, TLC terminator. 5.7 s. |
| `*.dsdfme.log` | dsd-fme stderr (with ANSI colour codes) from decoding the matching IQ through `Channelizer` with the production `ChannelPipeline.FilterProfile`, dsd-fme build 719d970 (Sep 2026). |
| `*.dsdfme-d6b64f3.log` | The same, from dsd-fme d6b64f3 (Nov 2026, what CI builds). This build doesn't print the DMR voice header, so the first voice burst comes before any talkgroup. The parser must cope with both orders. |

## Format

- Interleaved unsigned 8-bit I/Q, as written by `rtl_sdr` (centre 127.5).
- **96 000 samples/s.** Tune the channelizer 25 kHz *above* the channel: the channel sits at **−25 kHz** from centre.
- The voice is the test operator counting ("testing one two three…").

## How they were made

1. Captured with `rtl_sdr -f 158.650M -s 960000 -g 15.7 -p 0`, so the channel (158.400 MHz) sat
   at −250 kHz, the same offset FastScan uses for a single-channel bank. The handheld was a few feet from the
   dongle, so the ADC clips heavily during calls (20–50 % of samples). That's left in on purpose,
   because FM survives it and the decoders must too.
2. Each call was cut out, shifted from −250 kHz to −25 kHz, low-pass filtered (Blackman-windowed
   sinc, 38.4 kHz cutoff), decimated by 10 to 96 kS/s and requantized to u8. This keeps the files
   ~1 MB while the fixture still runs through the real channelizer.

The DMR capture is the one that exposed the FastScan scaling bug. Scaled so 1944 Hz hit 80 % of full scale, it
decoded no call LC, no colour code and no audio. Scaled at 5 kHz it decodes completely.
