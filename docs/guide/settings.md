# Settings

Open **Settings** from the header. Changes apply immediately.

## Transcription

Backend, model and thread count for speech-to-text. See [Transcription](transcription.md).

With a remote server that supports it, this section also has **AI call analysis** and **Analyze history**. See [Call map](map.md).

## Radios

Shown once OpenScanner has found a dongle. It lists every RTL-SDR it has seen, attached or not.

- **Channel assignment** — **Auto** divides channels between the dongles for you; **Manual** uses the SDR you pick for each channel. See [Multiple SDRs](multiple-sdrs.md#auto-and-manual-assignment).
- The summary line shows how the channels are covered, e.g. *5 of 7 channels fast-scanned across 2 SDRs · 2 hopping on SDR 2*.
- **Rescan** looks for dongles again straight away, after plugging one in.

Each dongle has a card:

| Setting | Description |
|---|---|
| **Name** | Your name for the dongle, e.g. `Roof`. |
| **On/off switch** | Stop using the dongle without unplugging it. |
| **Analog dB / Digital dB / Mixed dB** | Tuner gain when the dongle carries only analog, only digital, or both kinds of channel. `0` is automatic. See [Gain](multiple-sdrs.md#gain). |
| **PPM** | The dongle's crystal error. See [Frequency correction](#frequency-correction). |

The card also says what the dongle is doing: fast-scanning a range, hopping through some channels, or idle.

## SDR Tuning

Shown instead of **Radios** when no dongle can be listed (none is plugged in yet, or `rtl_test` is missing). These single settings then apply to the default dongle.

### Tuner Gain

The RTL-SDR tuner gain in dB. `0` uses the tuner's own automatic gain control, which suits most setups.

- **Raise it** if weak signals are being missed.
- **Lower it** if a strong nearby transmitter is overloading the receiver.

### Frequency Correction

The dongle's crystal error, in parts per million (ppm). Dongles without a temperature-compensated oscillator (TCXO) are commonly 20–60 ppm off. At VHF that is several kHz — enough to push a narrowband channel out of the passband and break digital decoding.

OpenScanner can measure it for you:

1. Open Settings while a strong, known-accurate signal is being received.
2. The measured error appears under **Frequency Correction**, or as an **Apply** button on the dongle's card under **Radios**.
3. Click **Apply**.

Each dongle has its own crystal, so with several dongles each has its own PPM.

## Notifications

Push [fire tone-outs](fire-tones.md#phone-notifications) to your phone with ntfy.

## Integrations

**PowerDMS Department** — your department's public PowerDMS slug. When set, the [history](history.md#powerdms-daily-log) links to each day's daily log. Leave blank to disable.

## Storage

Shows how much disk is used, and how much of that is recordings and the database.

**Delete All Recordings** removes every recording. This cannot be undone.

Recordings are also cleaned up automatically when the disk runs low; see [Automatic cleanup](history.md#automatic-cleanup).

## Diagnostics

Opens the two diagnostic tools, **System Debug** and **RF Spectrum Debug**. See [Diagnostic tools](../troubleshooting.md#diagnostic-tools) for what each one shows.

## About

The installed **Software Version** and the **Git Commit** it was built from. Include these when reporting a problem.
