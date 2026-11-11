#!/bin/bash
# finalize_update.sh — one-time bridge from a git-clone install to a release install.
#
# Releases are now prebuilt artifacts installed under /opt/openscanner (see
# install.sh), and the in-app updater swaps them in with apply_update.sh. This
# file exists only for devices still running a build from *before* that change.
# Their updater does `git reset --hard <tag>`, builds from source into a staging
# dir, and then runs this script from the freshly reset checkout:
#
#   systemd-run --collect --unit=openscanner-selfupdate --property=Type=oneshot \
#       /bin/bash <repo>/scripts/finalize_update.sh <staging> <publish> <unit>
#
# Instead of swapping that source build in, hand over to the release installer,
# which downloads the matching artifact and migrates data, certs and models out
# of this checkout. After that the device updates from artifacts and never runs
# this script again.
set -u

STAGING="${1:-}"
UNIT="${3:-openscanner}"
REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)

log() { echo "[bridge] $*"; }

# The old updater ran git and the build as root inside the operator's checkout.
# Hand the tree back so it is not left half root-owned.
restore_ownership() {
    [ "$(id -u)" -eq 0 ] || return 0
    local owner
    owner=$(stat -c '%U:%G' "$REPO_ROOT/.git" 2>/dev/null) || return 0
    case "$owner" in
        ""|root:*) return 0 ;;
    esac
    chown -R "$owner" "$REPO_ROOT" || log "WARN: chown failed"
}

# Whatever happens, never leave the service down. If the installer failed it has
# already put the old install back, so this restarts the old version.
cleanup() {
    restore_ownership
    if ! systemctl is-active --quiet "$UNIT"; then
        log "Ensuring $UNIT is started"
        systemctl start "$UNIT" || true
    fi
}
trap cleanup EXIT

# The source build the old updater just produced is not used.
[ -n "$STAGING" ] && [ -d "$STAGING" ] && rm -rf "$STAGING"

log "Migrating this install to release artifacts under /opt/openscanner"
if bash "$REPO_ROOT/scripts/install.sh" --migrate-from "$REPO_ROOT"; then
    log "Done. This checkout is no longer used."
else
    log "ERROR: migration failed; the previous install is unchanged. Re-run: sudo $REPO_ROOT/scripts/install.sh"
    exit 1
fi
