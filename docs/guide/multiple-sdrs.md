# Multiple SDRs

One RTL-SDR can capture about 2.4 MHz at a time. When all your channels fit in that window, OpenScanner **fast-scans**: it listens to every channel at once and never misses the start of a transmission. When they don't, the dongle has to **hop** from channel to channel, and anything that starts on another channel while it is listening somewhere else is missed.

Plugging in more dongles fixes that. OpenScanner divides the channels between them, so more of them are fast-scanned.

## Adding a dongle

Plug it in. OpenScanner checks for dongles every 15 seconds, or straight away when you press **Rescan** in [Settings → Radios](settings.md#radios). New dongles appear there as **SDR 2**, **SDR 3** and so on, and are put to work immediately.

Prefer a powered USB hub or separate ports: each dongle draws power and streams several MB/s.

### Give each dongle its own serial number

Most dongles leave the factory with the serial `00000001`. If two share a serial, OpenScanner can only tell them apart by USB port order, and their settings (name, gain, ppm) can swap if they are replugged in a different order. Settings shows a warning when this happens.

Set a unique serial on each, one at a time, with OpenScanner stopped:

```bash
sudo systemctl stop openscanner
rtl_eeprom -d 0 -s ROOF0001   # -d is the index rtl_test lists the dongle at
# unplug and replug the dongle, then repeat for the next one
sudo systemctl start openscanner
```

Serials made only of digits are fine; OpenScanner never passes a serial to the tools, only the dongle's current index.

## Auto and Manual assignment

Choose the mode under **Channel assignment** in [Settings → Radios](settings.md#radios).

### Auto

OpenScanner divides the channels for you, aiming to fast-scan as many as possible:

1. If every channel can be covered with one 2.4 MHz window per dongle, every dongle fast-scans a window.
2. Otherwise one dongle hops through whatever the best windows on the other dongles can't cover.

Coverage always comes first. When two layouts cover the same number of channels, OpenScanner picks the one that keeps analog and digital channels on separate dongles, so each can run at the gain that suits it (see [Gain](#gain)). A spare dongle is used to split a window holding both kinds into an analog dongle and a digital dongle.

Editing a channel only restarts the dongle carrying it; the others keep listening.

### Manual

You pick the SDR for each channel in [Manage Channels](channels.md#sdr): the **SDR** dropdown next to each channel. Each dongle fast-scans its channels if they fit in one window and hops through them if they don't.

Channels left on **Auto**, and channels pinned to a dongle that is unplugged or disabled, are still scanned. OpenScanner places them, in this order:

1. beside pinned channels of the same kind (analog or digital) that they fit next to;
2. on dongles with nothing pinned to them, planned as in Auto mode;
3. beside any pinned channels they fit next to, rather than hopping;
4. on a dongle that hops anyway.

These show **AUTO → SDR name** in Manage Channels. A channel is only left out when placing it would mean overriding one of your pins; it shows **NOT SCANNED** and Settings warns about it.

Pins are kept when you switch to Auto, ready for switching back.

## Gain

Each dongle has three gains, in dB (`0` uses the tuner's automatic gain control):

| Gain | Used when the dongle carries |
|---|---|
| **Analog** | only analog channels (NFM, AM) |
| **Digital** | only digital channels (P25, DMR) |
| **Mixed** | both |

Digital decoding usually wants less gain than analog listening. P25 and DMR are decoded symbol by symbol, and front-end overload or intermodulation from a strong nearby signal breaks decoding well before it would be audible on an analog channel. Analog benefits from extra gain on weak signals.

A good starting point is to leave all three on AGC, then:

- if digital audio is choppy or drops out while the signal is strong, lower **Digital**;
- if weak analog channels are missed, raise **Analog**;
- set **Mixed** between the two.

A hopping dongle listens to one channel at a time, so it uses each channel's own gain (analog or digital) as it goes.

## Frequency correction

Every dongle's crystal is off by a different amount, so each has its own **PPM**. While a strong, known-accurate signal is being fast-scanned on a dongle, its card in Settings offers **Apply** with the measured correction.

## Turning a dongle off

Switch a dongle off in Settings to stop using it without unplugging it, for example to keep it for other software. Its channels move to the remaining dongles.

## Diagnostics

[RF Spectrum Debug](../troubleshooting.md#rf-spectrum-debug) has an **SDR** picker when more than one dongle is attached. Only the chosen dongle stops scanning while the sweep runs.
