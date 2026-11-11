# Install

OpenScanner installs from a prebuilt release. You do not need to clone the repository or install .NET or Node on the device.

## Requirements

- A 64-bit Debian-based OS: Raspberry Pi OS (64-bit), Ubuntu or Debian.
- A 64-bit ARM or x86-64 CPU.
- An internet connection during install.
- `sudo` access.

!!! warning "32-bit Raspberry Pi OS is not supported"
    Releases are built for 64-bit only. Run `uname -m`: it should print `aarch64` or `x86_64`.

## Install the latest release

```bash
curl -fsSL https://github.com/briannippert/OpenScanner/releases/latest/download/install.sh | sudo bash
```

The installer:

1. Installs the system packages OpenScanner needs (RTL-SDR tools, ffmpeg, gpsd and others).
2. Builds the digital voice decoders, **mbelib** and **DSD-FME**, from source. This is a one-time step that takes a few minutes.
3. Downloads the release for your CPU and verifies its SHA-256 checksum.
4. Generates a self-signed HTTPS certificate.
5. Registers and starts the `openscanner` systemd service.
6. Downloads the Whisper speech-to-text model.

When it finishes it prints the address of the web UI.

!!! note "Why are the decoders compiled on your device?"
    mbelib's authors attach a patent notice to compiled binaries, so OpenScanner does not distribute them. The installer fetches the source and builds it locally instead.

## Open the web UI

Browse to `http://<your-device-ip>`, or `https://<your-device-ip>` for the secure version.

The HTTPS certificate is self-signed, so your browser shows a warning the first time. Accepting it is safe on your own network, and HTTPS gives smoother live audio.

Next: [First run](first-run.md).

## Installer options

Pass options after `bash -s --`:

```bash
curl -fsSL https://github.com/briannippert/OpenScanner/releases/latest/download/install.sh | sudo bash -s -- --no-model
```

| Option | What it does |
|---|---|
| `--version <tag>` | Install a specific release, e.g. `v0.1.300`, instead of the latest. |
| `--tarball <path>` | Install from a release tarball you already downloaded. Useful offline. |
| `--deps-only` | Install system packages and the radio decoders, then stop. |
| `--no-model` | Skip downloading the Whisper model. The server fetches it the first time it transcribes. |

## Where things live

Everything is under `/opt/openscanner`:

| Path | Contents | Survives updates |
|---|---|---|
| `app/` | The installed release | Replaced |
| `app.prev/` | The previous release, kept for rollback | Replaced |
| `data/` | Database, recordings, IQ samples | Yes |
| `certs/` | HTTPS certificate | Yes |
| `whisper/models/` | Whisper models | Yes |
| `config/appsettings.json` | Your configuration overrides | Yes |

## Managing the service

```bash
systemctl status openscanner        # is it running?
journalctl -u openscanner -f        # live logs
sudo systemctl restart openscanner  # restart
```

## Uninstall

```bash
sudo /opt/openscanner/app/scripts/uninstall.sh
```

This removes the service and the application but keeps your database, recordings, certificate and models. To delete those too, add `--purge`.

## macOS

OpenScanner runs on macOS for development and desktop use, built from source. See [Development](development.md#macos).
