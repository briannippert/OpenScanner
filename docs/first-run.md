# First run

This page takes you from a fresh install to hearing your first transmission.

## 1. Check the radio is detected

Plug in the RTL-SDR dongle and open the web UI. The header shows the radio's status:

- <span class="chip chip--ok">SDR READY</span> — the dongle was found.
- <span class="chip chip--error">SDR DISCONNECTED</span> — see [Troubleshooting](troubleshooting.md#sdr-disconnected).

## 2. Add a channel

A new install has no channels, so there is nothing to scan yet.

1. In the **Channel Control** panel, click the edit (pencil) button to open **Manage Channels**.
2. Add a channel and fill in at least:
    - **Name** — what you want to see on screen, e.g. `County Fire Dispatch`.
    - **Frequency (MHz)** — e.g. `154.4300`.
    - **Mode** — **P25 (Digital)**, **DMR (Digital)** or **NFM (Analog)**.
3. Save.

Scanning starts as soon as there is a channel. See [Channels](guide/channels.md) for every field.

!!! tip "Finding frequencies"
    Sites such as RadioReference list the frequencies and modes used by agencies in your area.

## 3. Listen

When a transmission comes in, the scanner stops on that channel, the main display shows its name and frequency, and audio plays in your browser. Use the volume control in the header.

Browsers block audio until you interact with a page, so click anywhere in the UI once after loading it.

Every transmission also appears in the history on the right, with a transcript shortly afterwards.

## 4. Tune it up

Once you are receiving something:

- **Weak or garbled digital audio?** Set the dongle's **Frequency Correction** in [Settings](guide/settings.md#sdr-tuning). Inexpensive dongles are often far enough off frequency to break digital decoding.
- **Want alerts for dispatches?** Set up [Fire tone-outs](guide/fire-tones.md).
- **Seeing radio IDs instead of names?** Add [Radio aliases](guide/aliases.md).
