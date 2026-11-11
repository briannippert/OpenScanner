# Transcription

OpenScanner transcribes each recording to text with [Whisper](https://github.com/ggerganov/whisper.cpp), running on the device. Transcripts appear in the history a short while after the transmission ends, and make the history searchable.

Configure it under **Settings → Transcription**.

## Backend

| Backend | Description |
|---|---|
| **Local (this device)** | Transcribe on the same machine. The default. |
| **Remote server** | Send audio to a separate OpenScanner Whisper server at a **Server URL** you provide. Use this to take the load off a slower device. |

## Model

Bigger models are more accurate and slower.

| Model | Trade-off |
|---|---|
| **Large v3 Turbo (q5)** | Most accurate. Recommended, and the default. |
| **Large v3 Turbo** | Most accurate, larger download. |
| **Medium (English)** | Accurate. |
| **Small (English)** | Balanced. |
| **Base (English)** | Fast. |
| **Tiny (English)** | Fastest, least accurate. |

When you pick a model that is not on the device yet, it is downloaded automatically; the setting shows the download status.

!!! tip "Slower hardware"
    On anything less powerful than a Raspberry Pi 5, the large models may fall behind during busy periods. Try **Small** or **Base**, or use a remote server.

## Transcription Threads

How many recordings are transcribed at once. With a large model, keep this at **1**: each run already uses every CPU core.

## Catching up on missed transcripts

If transcription was off or failing for a while, open **System Debug** from the header and use **Transcription Backfill**. It transcribes recordings from the last 24 hours that have no transcript, at low priority so live transmissions come first.
