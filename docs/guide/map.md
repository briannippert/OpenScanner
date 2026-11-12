# Call map

The call map puts dispatched calls on a map, live as they come in and from history. It needs **AI call analysis** (see below), which reads each transcript to find the call type, a one-line summary, and where the call is.

Open it from the **Call Map** button in the header, or search for "map" in the command palette (Ctrl+K / ⌘K). It opens in its own browser tab, so you can keep it on a second screen next to the dashboard.

## Turning on AI call analysis

AI call analysis runs on a remote OpenScanner Whisper server that supports it.

1. Go to **Settings → Transcription**, choose **Remote server** as the backend, and enter its **Server URL**.
2. OpenScanner checks whether the server offers call analysis. If it does, an **AI call analysis** switch appears. It stays hidden for servers that don't support it.
3. Turn the switch on.
4. Optionally, set a **Default location** (city and state). It is used to look up addresses when a call doesn't say which town it is in. Leave it blank to use the server's own default area.

From then on, every transcript is analyzed shortly after it is transcribed. Routine radio chatter ("10-4", "copy") is recognised and left off the map.

### Analyzing older calls

Calls transcribed before you turned the feature on are not on the map yet. Under the switch, **Analyze history → Start** works through them at low priority, pausing whenever live calls need analyzing. You can stop it at any time and start it again later; it carries on where it left off.

## Using the map

| Control | What it does |
|---|---|
| **1h · 6h · 24h · 7d · 30d** | How far back to show calls. |
| **All · Police · Fire/EMS** | Limit to one service. |
| **Markers · Heat · Both** | Show each call as a marker, as a heat map of where calls cluster, or both. |
| **Category chips** | Show or hide a group of call types. **Call types** lets you pick individual types. |
| **Follow new calls** | Jump to each new call as it arrives. |
| **Live audio** | Listen to the live scanner in this tab. Off by default, so the map doesn't double up with the dashboard. |

Markers are colored and shaped by category:

| Marker | Category | Call types |
|---|---|---|
| Orange square | Fire & hazard | Fire, alarm, hazard |
| Green diamond | Medical & rescue | Medical, rescue, welfare check |
| Blue circle | Police & traffic | Traffic stop, collision, assault, domestic, theft, burglary, suspicious activity, disturbance |
| Grey dot | Service & other | Public assist, other |

Click a marker, or a call in the list, to see its summary, address, channel and transcript. **Play recording** plays the call in the player at the bottom of the page.

### When a new call comes in

- Its marker pops onto the map with a ripple, and a card beside it shows the call type, summary and address for a few seconds. Click the card to open the full details.
- If the call is outside the part of the map you're looking at (or only the heat map is showing), a notice appears in the bottom-left corner instead, with an arrow pointing toward it. Tap it to jump there.
- With **Follow new calls** on, the map goes straight to each new call and opens its details.

If your system is set to reduce motion, the pop and ripple are skipped and cards simply appear and fade.

## Notes

- Only calls with a location that could be found on a map appear. A call with no address, or an address the server couldn't place, is still analyzed but not shown.
- Locations are estimates from the spoken address and may be off, especially for intersections or vague landmarks.
- The map tiles come from [OpenStreetMap](https://www.openstreetmap.org/copyright), so the browser viewing the map needs internet access.
