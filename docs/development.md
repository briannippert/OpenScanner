# Development

This page is for building OpenScanner from source. To just use it, see [Install](install.md).

## Layout

| Path | Contents |
|---|---|
| `server/OpenScanner.Server` | ASP.NET Core backend: SDR control, decoding, recording, transcription, API. |
| `server/OpenScanner.Tests` | Server tests. |
| `client/` | React + TypeScript web client (Vite, MUI). |
| `scripts/` | Installer, updater and release packaging. |
| `docs/` | This site. |

## Build and run on Linux

You need the **.NET 10 SDK** and **Node 22**. Building the server also builds the client.

```bash
git clone https://github.com/briannippert/OpenScanner.git
cd OpenScanner

# System libraries and the radio decoders
sudo ./scripts/install.sh --deps-only

# Whisper, for local transcription (optional)
git clone https://github.com/ggerganov/whisper.cpp.git
cmake -S whisper.cpp -B whisper.cpp/build
cmake --build whisper.cpp/build -j --config Release

cd server/OpenScanner.Server
dotnet run --urls "http://0.0.0.0:8080"
```

Then open `http://localhost:8080`.

Run from a checkout, the server keeps its data in `data/` at the repository root. Setting the `OPENSCANNER_HOME` environment variable switches it to the installed layout described in [Install](install.md#where-things-live).

## macOS

macOS is supported for development and desktop use. There is no background service.

```bash
git clone https://github.com/briannippert/OpenScanner.git
cd OpenScanner
./scripts/install_mac.sh

cd server/OpenScanner.Server
dotnet run -c Release --urls "http://0.0.0.0:8080"
```

The script needs [Homebrew](https://brew.sh) and macOS 13 or newer. It installs the dependencies and the .NET SDK, and builds whisper.cpp and DSD-FME from source.

## No radio? Use the mock source

OpenScanner ships a mock radio that plays back sample audio, so the whole UI works without a dongle. In `server/OpenScanner.Server/appsettings.Development.json`:

```json
"Radio": { "Provider": "Mock" }
```

## Tests

```bash
# Server
dotnet test server/OpenScanner.slnx -p:SkipFrontendBuild=true

# Client
cd client
npm ci
npm run lint
npm test -- --run
npm run test:e2e     # Playwright
```

## API

The server exposes a REST API, documented with Swagger at `/swagger` on any running instance, for example `http://<your-device-ip>/swagger`.

## Releases

Releases are cut manually: **Actions → Release → Run workflow** on `main`. The workflow builds a self-contained server, the web client and `whisper-cli` for `linux-x64` and `linux-arm64`, and publishes them with `SHA256SUMS` and `install.sh`. Devices pick new releases up from there.

Running the workflow from another branch publishes a **pre-release**, which devices ignore. Install one explicitly with `install.sh --version <tag>` to test it.

To build a release tarball locally:

```bash
scripts/build_whisper.sh /tmp/whisper
scripts/package_release.sh linux-x64 dist --whisper-dir /tmp/whisper
```

## Working on these docs

The site is built with [Material for MkDocs](https://squidfunk.github.io/mkdocs-material/).

```bash
python3 -m venv .venv && . .venv/bin/activate
pip install -r docs/requirements.txt
mkdocs serve
```

Then open `http://127.0.0.1:8000`. Pages live in `docs/`, navigation in `mkdocs.yml`, and the theme colors in `docs/stylesheets/openscanner.css`. Changes merged to `main` are published automatically.
