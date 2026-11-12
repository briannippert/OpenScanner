# Troubleshooting

Start with the service log. Most problems explain themselves there:

```bash
journalctl -u openscanner -n 100 --no-pager   # recent
journalctl -u openscanner -f                  # live
```

## The web UI does not load

Check the service is running:

```bash
systemctl status openscanner
```

- **Not running or restarting in a loop** — read the log above for the error.
- **Running but unreachable** — confirm you are using the device's current IP address (`hostname -I`) and that nothing else is using ports 80 or 443.

## SDR DISCONNECTED

The header shows <span class="chip chip--error">SDR DISCONNECTED</span> when no RTL-SDR dongle is found.

1. Re-seat the dongle. Prefer a USB port directly on the device over an unpowered hub.
2. Check the system sees it:

    ```bash
    lsusb | grep -i -E 'rtl|realtek'
    ```

3. Check nothing else is using it. Only one program can open the dongle at a time, so close any other SDR software.
4. Restart the service: `sudo systemctl restart openscanner`.

OpenScanner retries by itself if the dongle drops off USB and comes back.

With several dongles, the chip stays green while any of them is delivering, and its tooltip lists them. Check each dongle's card in [Settings → Radios](guide/settings.md#radios): an unplugged one shows **UNPLUGGED**, and one that is attached but not delivering samples shows **NO DATA**. Several dongles on one unpowered hub can brown out; use a powered hub.

## Two dongles keep swapping settings

They share a serial number, so OpenScanner can only tell them apart by USB port order. Give each its own serial with `rtl_eeprom`; see [Multiple SDRs](guide/multiple-sdrs.md#give-each-dongle-its-own-serial-number).

## No audio in the browser

- Click anywhere on the page once. Browsers block audio until you interact with the page.
- Check the volume control in the header, and your device's own volume.
- Use `https://` rather than `http://`. The secure page uses a smoother audio path.
- Make sure a transmission is actually being received: the scanner display should show an active channel.

## Receiving a signal but digital audio is garbled or silent

Most often the dongle is off frequency.

- Set **Frequency Correction** in [Settings](guide/settings.md#frequency-correction): the dongle's **PPM** under **Radios**. This is the single most common fix.
- Check the channel's **Mode** matches the system (P25, DMR or NFM).
- For DMR, check the **Color Code** and **Slot**.
- Try adjusting the gain: too low misses weak signals, too high overloads on strong ones. Digital channels often decode better with less gain; with **Radios** you can set the **Digital** gain on its own.

## No transcripts

- Open **System Debug** (Settings → Diagnostics) and look at **Transcription Queue** and the service statuses.
- The first transcription after an install, or after changing the model, waits for the model to download. Check the model status in **Settings → Transcription**.
- On slower hardware a large model can fall behind. Try a smaller [model](guide/transcription.md#model).
- To transcribe recordings that were missed, use [Transcription Backfill](guide/transcription.md#catching-up-on-missed-transcripts).

## The disk is filling up

OpenScanner removes the oldest non-favorite recordings when free space drops below 2 GB. To free space sooner, delete recordings from the history or use **Delete All Recordings** in [Settings → Storage](guide/settings.md#storage).

## An update failed

A failed update rolls back to the previous version by itself, so the scanner keeps running. To see why it failed:

```bash
journalctl -u openscanner-selfupdate -n 50 --no-pager
```

The release that failed to start is kept in `/opt/openscanner/app.failed` for inspection. You can retry from **Software Update**, or [re-run the installer](updating.md#from-the-command-line).

## The browser warns about the certificate

That is expected. The HTTPS certificate is self-signed, generated on your device during install. Accept the warning once per browser.

## Diagnostic tools

Both are under **Settings → Diagnostics**. You can also open either from the [command palette](guide/dashboard.md#command-palette).

### System Debug

A live view of the device and the service:

- CPU, memory and temperature.
- The transcription queue and backfill.
- Service statuses and listening ports.
- SDR and scanner state: frequency, signal, gain, squelch, throughput and restart count.
- GPS status.
- Database totals.

**Download Support Package** bundles logs and diagnostics into one file. Attach it when you report a problem.

### RF Spectrum Debug

A live waterfall of the raw RF around a center frequency, for checking that a signal is really there and where it sits.

!!! warning "Scanning pauses while this is open"
    RF Spectrum Debug needs exclusive use of the dongle it sweeps with. That dongle stops scanning and decoding until you close it. With several dongles, pick which one to use from the **SDR** list; the others keep scanning.

The spike in the middle of the display is a DC offset common to RTL-SDR hardware, not a real signal.

## Reporting a problem

Open an issue at [github.com/briannippert/OpenScanner/issues](https://github.com/briannippert/OpenScanner/issues) with:

- The **Software Version** and **Git Commit** from [Settings → About](guide/settings.md#about).
- What you expected and what happened.
- A support package from **System Debug**.
