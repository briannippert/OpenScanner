# Fire tone-outs

Fire and EMS dispatch often starts with a two-tone page that alerts a specific station. OpenScanner can listen for the tone sets you define and flag them when they go out.

## Add a tone set

Open **Fire Tone Outs** from the header, then add a tone set:

| Field | Description |
|---|---|
| **Name** | What to show when it is detected, e.g. `Station 4`. |
| **Tone A (Hz)** | The first tone's frequency. |
| **Tone B (Hz)** | The second tone's frequency. Leave blank for a single long tone. |
| **Description** | Optional note. |

## When a tone-out is detected

- The scanner display shows <span class="chip chip--warn">ALERT</span> with the tone set's name.
- The event appears in the **Fire tone-outs** panel above the history. Click an event to jump to its recording.
- The recording is tagged, so the **Tone-outs** filter in the [history](history.md#views) finds it later.

The fire tone-outs panel only appears once you have defined at least one tone set.

## Phone notifications

OpenScanner can push each tone-out to your phone through [ntfy](https://ntfy.sh), with the dispatch transcript as the message. Set it up under **Settings → Notifications**:

1. Turn on **Push tone-outs via ntfy**.
2. Choose a **Topic** and subscribe to the same topic in the ntfy app.
3. Leave **Server URL** blank to use the public `ntfy.sh` server, or enter your own.
4. Add an **Access Token** if your topic or server requires one.

!!! warning "ntfy.sh topics are public"
    Anyone who knows the topic name can read its messages. Pick a long, hard-to-guess name, or use a protected topic.
