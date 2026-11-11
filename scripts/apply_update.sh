#!/bin/bash
# apply_update.sh — swap an unpacked release into place and restart the service.
#
#   apply_update.sh <new_app_dir> <install_root> [unit]
#
# <new_app_dir> is an extracted release (the tarball's openscanner/ directory),
# <install_root> is e.g. /opt/openscanner. The current <install_root>/app is kept
# as app.prev; if the new version does not come up healthy it is rolled back.
#
# The in-app updater runs the copy of this script that ships *inside the release
# being installed*, detached in its own transient systemd unit so that stopping the
# service does not kill it mid-swap:
#
#   systemd-run --collect --unit=openscanner-selfupdate --property=Type=oneshot \
#       /bin/bash <new_app_dir>/scripts/apply_update.sh <new_app_dir> <install_root> openscanner
#
# install.sh uses it for the same swap. Must run as root.
set -u

NEW="${1:?new app dir required}"
HOME_DIR="${2:?install root required}"
UNIT="${3:-openscanner}"
APP="$HOME_DIR/app"
PREV="$HOME_DIR/app.prev"
# How long the service must stay up, without systemd restarting it, to count as healthy.
SETTLE_SECONDS="${OPENSCANNER_SETTLE_SECONDS:-15}"

log() { echo "[apply-update] $*"; }

if [ ! -x "$NEW/server/OpenScanner.Server" ]; then
    log "ERROR: '$NEW' is not an unpacked release (no server/OpenScanner.Server); nothing changed"
    exit 1
fi

restarts() { systemctl show -p NRestarts --value "$UNIT" 2>/dev/null || echo 0; }

# Start the unit and require it to stay active for SETTLE_SECONDS without a crash
# loop. The unit has Restart=always, so "active right now" alone proves nothing.
start_and_verify() {
    systemctl start "$UNIT" || return 1
    local before i
    before=$(restarts)
    for ((i = 0; i < SETTLE_SECONDS; i++)); do
        sleep 1
        [ "$(restarts)" = "$before" ] || return 1
        systemctl is-failed --quiet "$UNIT" && return 1
    done
    systemctl is-active --quiet "$UNIT"
}

log "Stopping $UNIT"
systemctl stop "$UNIT" 2>/dev/null || true

rm -rf "$PREV"
HAD_PREVIOUS=false
if [ -d "$APP" ]; then
    mv "$APP" "$PREV" || { log "ERROR: could not move the current version aside; nothing changed"; systemctl start "$UNIT"; exit 1; }
    HAD_PREVIOUS=true
fi

log "Installing $(cat "$NEW/VERSION" 2>/dev/null || echo 'new version')"
if mv "$NEW" "$APP" && start_and_verify; then
    log "$UNIT is up on the new version"
    rm -rf "$HOME_DIR/staging"
    exit 0
fi

log "ERROR: the new version did not start cleanly"
systemctl stop "$UNIT" 2>/dev/null || true
if [ "$HAD_PREVIOUS" = true ]; then
    log "Rolling back to the previous version"
    rm -rf "$HOME_DIR/app.failed"
    [ -d "$APP" ] && mv "$APP" "$HOME_DIR/app.failed"
    mv "$PREV" "$APP"
    # The crash loop above may have tripped systemd's start rate limit.
    systemctl reset-failed "$UNIT" 2>/dev/null || true
    systemctl start "$UNIT" || log "ERROR: the previous version failed to start too"
    log "Rolled back. The failed version is kept in $HOME_DIR/app.failed; see: journalctl -u $UNIT"
else
    log "No previous version to roll back to; see: journalctl -u $UNIT"
fi
rm -rf "$HOME_DIR/staging"
exit 1
