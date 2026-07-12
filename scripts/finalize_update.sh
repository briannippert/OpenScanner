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

log() { echo "[finalize] $*"; }

# Whatever happens, never leave the service down: always try to start it on exit.
cleanup() {
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
