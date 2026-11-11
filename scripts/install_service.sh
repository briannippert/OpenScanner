#!/bin/bash
# install_service.sh — superseded by install.sh.
#
# OpenScanner used to be built from this checkout. It now installs from prebuilt
# release artifacts into /opt/openscanner. This wrapper keeps the old command
# working: it runs the release installer and migrates this checkout's data,
# certificates and Whisper models to the new location.
set -e
SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
echo "[INFO] install_service.sh now installs the latest release (it no longer builds from this checkout)."
exec bash "$SCRIPT_DIR/install.sh" --migrate-from "$(dirname "$SCRIPT_DIR")" "$@"
