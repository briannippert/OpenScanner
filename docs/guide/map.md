# Call map

The call map puts dispatched calls on a map, live as they come in and from history. It needs **AI call analysis** (see below), which reads each transcript to find the call type, a one-line summary, and where the call is.

Open it from the **Call Map** button in the header, or search for "map" in the command palette (Ctrl+K / ⌘K). It opens in its own browser tab, so you can keep it on a second screen next to the dashboard.

## Turning on AI call analysis

AI call analysis runs on an [OpenScanner AI server](ai-server.md). Set one up first.

1. Go to **Settings → Transcription**, choose **Remote server** as the backend, and enter its **Server URL**.
2. OpenScanner checks whether the server offers call analysis. If it does, an **AI call analysis** switch appears. It stays hidden for servers that don't support it.
3. Turn the switch on.
4. Optionally, set a **City** and **State** on each channel (**Channels → Edit**). They are used to look up addresses when a call on that channel doesn't say which town it is in, so a scanner that hears several towns places each call in the right one. Leave them blank to use the server's own default area.

From then on, every transcript is analyzed shortly after it is transcribed. Routine radio chatter ("10-4", "copy") is recognised and left off the map.

### Analyzing older calls

Calls transcribed before you turned the feature on are not on the map yet. Under the switch, **Analyze history → Start** works through them at low priority, pausing whenever live calls need analyzing. You can stop it at any time and start it again later; it carries on where it left off.

## Using the map

| Control | What it does |
|---|---|
| **1h · 6h · 24h · 7d · 30d · All time** | How far back to show calls. |
| **Custom** | Pick a start and (optionally) an end date and time. With no end, new calls keep arriving live; with an end in the past, only that window is shown. Click the date chip to change it. |
| **All · Police · Fire/EMS** | Limit to one service. |
| **Markers · Heat · Both** | Show each call as a marker, as a heat map of where calls cluster, or both. Heat runs from purple-blue (a single call) through pink and orange to yellow (many calls in one spot). |
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

### One marker per incident

Dispatch, en route and on-scene traffic about the same call are grouped into one incident, so the same address doesn't fill up with dots. Calls are grouped when they come from the same service (police, or fire/EMS), are within about 100 m of each other, and are no more than 15 minutes apart. A police call and a fire call at the same address stay separate, so you see both.

The marker and list row show the latest status, and a **N calls** badge counts the calls in the incident. Select the incident in the list to see each call with its own play button. The heat map counts incidents, not individual transmissions.

### When a new call comes in

- Its marker pops onto the map with a ripple, and a card beside it shows the call type, summary and address for a few seconds. Click the card to open the full details.
- If the call is outside the part of the map you're looking at (or only the heat map is showing), a notice appears in the bottom-left corner instead, with an arrow pointing toward it. Tap it to jump there.
- With **Follow new calls** on, the map goes straight to each new call and opens its details.

If your system is set to reduce motion, the pop and ripple are skipped and cards simply appear and fade.

## Notes

- Only calls with a location that could be found on a map appear. A call with no address, or an address the server couldn't place, is still analyzed but not shown.
- Only the newest 5,000 calls in a range are loaded. For long ranges, such as **All time**, pick a shorter or custom range to see older calls.
- Locations are estimates from the spoken address and may be off, especially for intersections or vague landmarks.
- The map tiles come from [OpenStreetMap](https://www.openstreetmap.org/copyright), so the browser viewing the map needs internet access.
