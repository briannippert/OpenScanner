# OpenScanner - Hardware Setup Guide

You have successfully configured the code to use **Real Hardware**.
However, since I observed that `rtl_sdr` and `dsd` are missing from your path, you **must install them** for the scanner to work.

## 1. Install RTL-SDR Drivers (Required)
This provides the `rtl_power` and `rtl_fm` tools used to scan frequencies.

```bash
sudo apt-get update
sudo apt-get install rtl-sdr
```

**Test it:**
Plug in your USB dongle and run:
```bash
rtl_test
```
*If it says "device not found" or "permissions denied", you may need to setup udev rules (usually handled by the package).*

## 2. Running with Real Hardware
To tell the server to use the real driver instead of the simulation, set the environment variable:

```bash
cd OpenScanner/server
USE_REAL_RADIO=true npx ts-node src/index.ts
```

## 3. P25 Audio Decoding (Advanced)
The current implementation only *detects* signals. To actually hear the P25 digital audio, you need a digital decoder. The standard tool is `dsd` (Digital Speech Decoder) or `dsdcc`.

1.  **Install DSD:** (This often requires compiling from source on Linux as it's not always in default repos, or look for `snap` packages).
    *   https://github.com/szechyjs/dsd
2.  **Enable Piping:**
    *   Once installed, you would modify `src/scanner/RtlDevice.ts` to spawn `rtl_fm` and pipe its output to `dsd`.

## Troubleshooting
*   **Error: rtl_power not found:** You didn't install `rtl-sdr`.
*   **Device busy:** Make sure no other software (like GQRX or SDR#) is using the dongle.
