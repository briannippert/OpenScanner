# Settings

Open **Settings** from the header. Changes apply immediately.

## Transcription

Backend, model and thread count for speech-to-text. See [Transcription](transcription.md).

## SDR Tuning

### Tuner Gain

The RTL-SDR tuner gain in dB. `0` uses the tuner's own automatic gain control, which suits most setups.

- **Raise it** if weak signals are being missed.
- **Lower it** if a strong nearby transmitter is overloading the receiver.

### Frequency Correction

The dongle's crystal error, in parts per million (ppm). Dongles without a temperature-compensated oscillator (TCXO) are commonly 20–60 ppm off. At VHF that is several kHz — enough to push a narrowband channel out of the passband and break digital decoding.

OpenScanner can measure it for you:

1. Open Settings while a strong, known-accurate signal is being received.
2. The measured error appears under **Frequency Correction**.
3. Click **Apply**.

## Notifications

Push [fire tone-outs](fire-tones.md#phone-notifications) to your phone with ntfy.

## Integrations

**PowerDMS Department** — your department's public PowerDMS slug. When set, the [history](history.md#powerdms-daily-log) links to each day's daily log. Leave blank to disable.

## Storage

Shows how much disk is used, and how much of that is recordings and the database.

**Delete All Recordings** removes every recording. This cannot be undone.

Recordings are also cleaned up automatically when the disk runs low; see [Automatic cleanup](history.md#automatic-cleanup).

## About

The installed **Software Version** and the **Git Commit** it was built from. Include these when reporting a problem.
