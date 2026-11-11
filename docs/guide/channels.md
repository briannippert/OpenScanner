# Channels

A channel is one frequency for the scanner to listen on. Open **Manage Channels** from the edit button in the **Channel Control** panel, or from the [command palette](dashboard.md#command-palette).

## Channel fields

| Field | Description |
|---|---|
| **Name** | Shown on the scanner display and in the history. |
| **Frequency (MHz)** | The channel frequency, e.g. `154.4300`. |
| **Description** | Optional note shown under the name in lists. |
| **Mode** | How to decode the channel. See below. |
| **Tone** | Optional squelch tone for the channel, e.g. `100.0`. |
| **License** | Optional reference, such as the FCC callsign. |
| **Tag** | Optional label, such as `Fire` or `EMS`. |
| **Avoid Channel** | Leave the channel out of the scan without deleting it. |

## Modes

| Mode | Use for |
|---|---|
| **P25 (Digital)** | P25 Phase 1 digital voice, common for public safety. |
| **DMR (Digital)** | DMR digital voice. Adds the fields below. |
| **NFM (Analog)** | Conventional narrowband FM. Shown as **ANALOG** on the display. |

### DMR options

DMR channels have three extra fields that narrow down what you hear:

| Field | Description |
|---|---|
| **Slot** | **Slot 1**, **Slot 2**, or **Any**. |
| **Color Code (0–15)** | The repeater's color code. |
| **Talkgroup ID** | Only follow this talkgroup. |

## Hold, avoid and skip

- **Hold** keeps the scanner on one channel until you resume.
- **Avoid** removes a channel from the scan until you clear it. It is the same switch as **Avoid Channel** in the editor.
- **Skip** drops only the transmission you are hearing now.

## Deleting a channel

Deleting a channel removes it from the scan. If you only want to stop hearing it for a while, use **Avoid** instead.
