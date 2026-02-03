#!/bin/bash
set -e

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color
BOLD='\033[1m'

log_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
log_success() { echo -e "${GREEN}[OK] $1${NC}"; }
log_error() { echo -e "${RED}[ERROR] $1${NC}"; }

# Check NOT Root
if [ "$EUID" -eq 0 ]; then
  log_error "Please run as a regular user (NOT root)."
  log_error "The script will ask for sudo password when needed."
  exit 1
fi

# Ensure sudo is available
if ! command -v sudo &> /dev/null; then
    log_error "This script requires 'sudo' to remove the system service."
    exit 1
fi

# Refresh sudo credentials upfront
sudo -v

SERVICE_NAME="openscanner"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"

echo "========================================="
echo " OpenScanner Service Uninstaller"
echo "========================================="

# 1. Stop and Disable Service
if systemctl list-unit-files | grep -q "${SERVICE_NAME}.service"; then
    log_info "Stopping ${SERVICE_NAME} service..."
    sudo systemctl stop ${SERVICE_NAME} || true
    
    log_info "Disabling ${SERVICE_NAME} service..."
    sudo systemctl disable ${SERVICE_NAME} || true
else
    log_info "Service ${SERVICE_NAME} is not installed or already removed."
fi

# 2. Remove Service File
if [ -f "$SERVICE_FILE" ]; then
    log_info "Removing service file: $SERVICE_FILE"
    sudo rm "$SERVICE_FILE"
    log_success "Service file removed."
else
    log_info "Service file not found."
fi

# 3. Reload Daemon
log_info "Reloading systemd daemon..."
sudo systemctl daemon-reload

log_success "Uninstallation of service complete."
echo "Note: This script only removed the systemd service. The code and dependencies remain in this directory."
