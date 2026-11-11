# Updating

## From the web UI

When a newer release is available, an <span class="chip chip--warn">UPDATE</span> badge appears in the header.

1. Open **Software Update** from the header.
2. Click **Update**.

OpenScanner downloads the release, verifies its SHA-256 checksum, swaps it in and restarts. The page reconnects by itself, usually within a minute.

You can open **Software Update** at any time and check for a release on demand; OpenScanner also checks every 30 minutes.

!!! success "Failed updates roll back"
    If the new version does not start cleanly, the previous version is restored automatically. Your recordings, database and settings are never part of the swap.

## From the command line

Re-running the installer moves the device to the latest release and repairs the install:

```bash
curl -fsSL https://github.com/briannippert/OpenScanner/releases/latest/download/install.sh | sudo bash
```

To install a specific release, including going back to an older one:

```bash
curl -fsSL https://github.com/briannippert/OpenScanner/releases/latest/download/install.sh | sudo bash -s -- --version v0.1.300
```

## Upgrading from a git-clone install

Older versions of OpenScanner were built on the device from a git clone. Those installs move to the release layout on their own:

- Click **Update** in the web UI, **or**
- re-run `./scripts/install_service.sh` in the old checkout.

This first upgrade takes longer than usual. Your database, recordings, certificate, Whisper models and PowerDMS setting are moved to `/opt/openscanner`. Afterwards the old checkout is no longer used and can be deleted.

If the migration fails, the old install is put back as it was.
