# OpenScanner

[![.NET CI](https://github.com/briannippert/OpenScanner/actions/workflows/dotnet.yml/badge.svg)](https://github.com/briannippert/OpenScanner/actions/workflows/dotnet.yml)
[![Frontend Build and Lint](https://github.com/briannippert/OpenScanner/actions/workflows/frontend.yml/badge.svg)](https://github.com/briannippert/OpenScanner/actions/workflows/frontend.yml)

OpenScanner is a high-performance, web-based digital radio scanner designed for the Raspberry Pi (optimized for Pi 5). It combines a robust **.NET backend** with a modern **React frontend** to provide real-time P25 digital voice decoding, live spectrum analysis, and AI-powered transcriptions.

![OpenScanner UI](screenshot.png)

## Key Features

- **Digital Voice Decoding**: Stable P25 Phase 1 decoding via `rtl_fm` and `dsd-fme`.
- **AI Transcriptions**: Integrated **Whisper AI** (`whisper.cpp`) for automatic speech-to-text logging.
- **Fire Tone Out Detection**: Detects and alerts on specific 2-tone paging sequences (e.g., Fire/EMS dispatch).
- **Real-Time Synchronization**: State (locks, holds, status) is synced instantly across all connected browsers.
- **Modern Dashboard**:
    - **Live Waterfall**: Visual scrolling spectrogram of decoded audio.
    - **Transmission History**:
        - **Hierarchical Tree View**: Organize logs by Year, Month, Day, and Channel for easy browsing of massive datasets.
        - **Live Activity**: A dedicated "Recent Activity" section for monitoring incoming transmissions in real-time.
        - **Integrated Search**: Powerful search bar to find transmissions by transcription text, channel names, or frequencies.
        - **Instant Playback**: One-click listening for all recorded transmissions.
    - **Channel Management**: Full database control to add, edit, or delete channels. Includes frequency hold and scan resume.
- **GPS Integration**: Live tracking of location, altitude (Imperial units), and satellite count.
- **Swagger Documentation**: Full OpenAPI/Swagger UI for API exploration.
- **Service-Ready**: Automated installer configures OpenScanner as a systemd service on port 80.

## Hardware Requirements

- **Raspberry Pi**: (Pi 5 highly recommended for Whisper AI performance).
- **RTL-SDR Dongle**: (RTL2832U based, such as RTLSDRv3 or v4).
- **GPS Receiver**: (Optional, any USB GPS receiver compatible with `gpsd`).
- **Antenna**: Tuned for your local 150-800MHz public safety bands.

## Quick Start

### 1. Clone & Install
The unified installer handles everything: it installs required system libraries and radio drivers (DSD-FME, mbelib), builds the .NET backend and React frontend, and sets up Whisper AI.

```bash
# Clone the repository
git clone https://github.com/briannippert/OpenScanner.git
cd OpenScanner

# Run the installer (do NOT run with sudo, it will prompt for password when needed)
chmod +x scripts/*.sh
./scripts/install_service.sh
```

### 2. Access the UI
Once installed, open your browser to:
`http://<your-pi-ip>`

For API documentation, visit:
`http://<your-pi-ip>/swagger`

## Installation Options

- **Full Install**: `./scripts/install_service.sh` (Builds everything and installs the systemd service).
- **Dependencies Only**: `./scripts/install_service.sh --deps-only` (Installs all required system libraries, radio drivers, and Whisper models, then exits without building the app or installing the service).

## Running on macOS

OpenScanner also runs on macOS (Apple Silicon and Intel) for development and desktop use. The macOS setup script uses **Homebrew** to install dependencies, builds whisper.cpp and DSD-FME from source, builds the app, and prints how to run it. macOS has no systemd, so it does **not** install a background service.

```bash
git clone https://github.com/briannippert/OpenScanner.git
cd OpenScanner
chmod +x scripts/*.sh

# Installs deps (Homebrew + .NET 10 + whisper.cpp + dsd-fme) and builds the app
./scripts/install_mac.sh

# Dependencies only:
./scripts/install_mac.sh --deps-only
```

Then run the app and open `http://localhost:8080` (Swagger at `/swagger`):

```bash
cd server/OpenScanner.Server
dotnet run -c Release --urls "http://0.0.0.0:8080"
```

**Requirements:** [Homebrew](https://brew.sh) and macOS 13+. The script installs the rest (`rtl-sdr`, `ffmpeg`, `coreutils`, the .NET 10 SDK, etc.).

**No SDR hardware?** OpenScanner ships with a mock radio source so you can run the full UI without a dongle. Set the provider to `Mock` in `server/OpenScanner.Server/appsettings.Development.json`:

```json
"Radio": { "Provider": "Mock" }
```

**GPS (optional):** OpenScanner connects to `gpsd` on `localhost:2947`; install and start it via `brew install gpsd` if you want live location.

> **Note on external tools:** OpenScanner resolves `rtl_sdr`, `rtl_fm`, `ffmpeg`, and `dsd-fme` from your `PATH` and common install locations (Homebrew's `/opt/homebrew/bin` and `/usr/local/bin`, plus the standard Linux paths), so the same code runs on both the Raspberry Pi and macOS.

## Updating

To update OpenScanner to the latest version, simply navigate to the project directory and re-run the installation script. This will pull the latest changes, rebuild components, and restart the service:

```bash
./scripts/install_service.sh
```

## Management

- **Status Check**: `systemctl status openscanner`
- **View Logs**: `journalctl -u openscanner -f`
- **Uninstall Service**: `./scripts/uninstall_service.sh`
- **Data Location**: Database and recordings are stored in the `/data` directory in the project root.

## Acknowledgments

- [DSD-FME](https://github.com/lwvmobile/dsd-fme): Digital voice decoding.
- [whisper.cpp](https://github.com/ggerganov/whisper.cpp): High-performance local AI transcription.
- [FftSharp](https://github.com/swharden/FftSharp): Fast FFT calculations for the live spectrum.
- [NSwag](https://github.com/RicoSuter/NSwag): OpenAPI/Swagger integration.
