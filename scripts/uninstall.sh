#!/bin/bash
# uninstall.sh — remove the OpenScanner service and application.
#
#   sudo /opt/openscanner/app/scripts/uninstall.sh [--purge]
#
# By default your database, recordings, certificate, Whisper models and config
# under /opt/openscanner are kept, so a later install picks up where you left off.
# --purge deletes the whole install root as well.
#
# System packages and the dsd-fme / mbelib binaries in /usr/local are left alone.
set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; BLUE='\033[0;34m'; NC='\033[0m'
log_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
log_success() { echo -e "${GREEN}[OK] $1${NC}"; }
log_error() { echo -e "${RED}[ERROR] $1${NC}" >&2; }

HOME_DIR="${OPENSCANNER_HOME:-/opt/openscanner}"
UNIT="openscanner"
SERVICE_FILE="/etc/systemd/system/$UNIT.service"

PURGE=false
for arg in "$@"; do
    case "$arg" in
        --purge) PURGE=true ;;
        *) log_error "Unknown option: $arg"; exit 2 ;;
    esac
done

if [ "$EUID" -ne 0 ]; then
    if command -v sudo > /dev/null; then
        # Run from a copy: the script lives inside the directory it is about to remove.
        TMP_COPY=$(mktemp)
        cat "${BASH_SOURCE[0]}" > "$TMP_COPY"
        exec sudo OPENSCANNER_HOME="$HOME_DIR" bash "$TMP_COPY" "$@"
    fi
    log_error "Please run as root."
    exit 1
fi

echo "========================================="
echo " OpenScanner Uninstaller"
echo "========================================="

if systemctl list-unit-files 2>/dev/null | grep -q "^$UNIT.service"; then
    log_info "Stopping and disabling $UNIT..."
    systemctl stop "$UNIT" || true
    systemctl disable --quiet "$UNIT" || true
else
    log_info "Service $UNIT is not installed."
fi

if [ -f "$SERVICE_FILE" ]; then
    rm -f "$SERVICE_FILE"
    systemctl daemon-reload
    log_success "Service file removed."
fi

case "$HOME_DIR" in
    ""|/|/opt|/usr|/home|/root) log_error "Refusing to remove files under '$HOME_DIR'."; exit 1 ;;
esac

if [ "$PURGE" = true ]; then
    log_info "Removing $HOME_DIR (including data, recordings and models)..."
    rm -rf "$HOME_DIR"
    log_success "OpenScanner and all of its data removed."
else
    rm -rf "$HOME_DIR/app" "$HOME_DIR/app.prev" "$HOME_DIR/app.failed" "$HOME_DIR/staging"
    log_success "OpenScanner removed."
    if [ -d "$HOME_DIR" ]; then
        echo "Your data, certificate, models and config are kept in $HOME_DIR."
        echo "Delete them too with: sudo rm -rf $HOME_DIR"
    fi
fi
