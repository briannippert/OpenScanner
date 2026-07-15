#!/bin/bash
# finalize_update.sh — swap a freshly-built server into place and restart the service.
#
# Invoked (detached, in its own transient systemd unit) by UpdateService after a
# successful staging build, so that stopping the openscanner service does not kill
# this script mid-swap:
#
#   systemd-run --collect --unit=openscanner-selfupdate --property=Type=oneshot \
#       /bin/bash <repo>/scripts/finalize_update.sh <staging> <publish> <unit>
#
# Args: $1=staging dir  $2=publish dir  $3=systemd unit (default: openscanner)
set -u

STAGING="${1:?staging dir required}"
PUBLISH="${2:?publish dir required}"
UNIT="${3:-openscanner}"
BACKUP="${PUBLISH%/}_backup"
REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)

log() { echo "[finalize] $*"; }

# The service — and therefore the whole self-update — runs as root, while the
# checkout belongs to the operator. Every step that writes into the repo (git
# reset, npm build, the swap below) leaves root-owned files behind, which then
# block the next update and any manual `install_service.sh` run. Hand the tree
# back to whoever owns .git so the repo is never left half root-owned.
restore_ownership() {
    [ "$(id -u)" -eq 0 ] || return 0
    local owner
    owner=$(stat -c '%U:%G' "$REPO_ROOT/.git" 2>/dev/null) || return 0
    case "$owner" in
        ""|root:*) return 0 ;;
    esac
    log "Restoring ownership of $REPO_ROOT to $owner"
    chown -R "$owner" "$REPO_ROOT" || log "WARN: chown failed"
}

# Whatever happens, never leave the service down or the repo root-owned: the
# build steps have already run as root by the time we get here, so every exit
# path — including the aborts below — needs the ownership fixup.
cleanup() {
    restore_ownership
    if ! systemctl is-active --quiet "$UNIT"; then
        log "Ensuring $UNIT is started"
        systemctl start "$UNIT" || true
    fi
}
trap cleanup EXIT

if [ ! -d "$STAGING" ] || [ -z "$(ls -A "$STAGING" 2>/dev/null)" ]; then
    log "ERROR: staging dir '$STAGING' is missing or empty; aborting swap"
    exit 1
fi

log "Stopping $UNIT"
systemctl stop "$UNIT" || true

# Keep the previous build for rollback.
if [ -d "$PUBLISH" ]; then
    rm -rf "$BACKUP"
    cp -a "$PUBLISH" "$BACKUP" || log "WARN: could not snapshot previous build"
fi

log "Swapping in new build"
if command -v rsync >/dev/null 2>&1; then
    if ! rsync -a --delete "$STAGING"/ "$PUBLISH"/; then
        log "ERROR: rsync failed; rolling back"
        [ -d "$BACKUP" ] && rsync -a --delete "$BACKUP"/ "$PUBLISH"/
    fi
else
    mkdir -p "$PUBLISH"
    rm -rf "${PUBLISH:?}/"*
    if ! cp -a "$STAGING"/. "$PUBLISH"/; then
        log "ERROR: cp failed; rolling back"
        [ -d "$BACKUP" ] && cp -a "$BACKUP"/. "$PUBLISH"/
    fi
fi

chmod +x "$PUBLISH/OpenScanner.Server" 2>/dev/null || true

log "Starting $UNIT"
systemctl start "$UNIT"
log "Done"
