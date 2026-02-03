#!/bin/bash
set -e

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color
BOLD='\033[1m'

log_info() { echo -e "${BLUE}[TEST INFO]${NC} $1"; }
log_success() { echo -e "${GREEN}[TEST SUCCESS] $1${NC}"; }
log_error() { echo -e "${RED}[TEST ERROR] $1${NC}"; }

# Check NOT Root
if [ "$EUID" -eq 0 ]; then
  log_error "Please run as a regular user (NOT root)."
  exit 1
fi

# Ensure sudo works
sudo -v

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
PROJECT_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

# Ensure we are in the project root for relative path cleanup
cd "$PROJECT_ROOT"

# 0. Cleanup / Reset (Force Fresh Builds)
log_info "Step 0: Cleaning up environment to force fresh builds..."

# --- Whisper.cpp ---
if [ -d "whisper.cpp" ]; then
    log_info "Removing whisper.cpp source..."
    rm -rf whisper.cpp
fi

# --- Radio Dependencies (dsd-fme & mbelib) ---
if [ -d "build_deps" ]; then
    log_info "Removing build_deps source..."
    rm -rf build_deps
fi

# Remove installed binaries/libs to trigger the installer's "missing" checks
if command -v dsd-fme &> /dev/null; then
    log_info "Removing installed dsd-fme binary..."
    sudo rm -f "$(which dsd-fme)"
fi

log_info "Removing installed mbelib libraries..."
sudo rm -f /usr/local/lib/libmbe*
sudo rm -f /usr/local/include/mbelib.h
sudo rm -f /usr/include/mbelib.h
sudo ldconfig # Update shared library cache

# --- Application Builds ---
log_info "Cleaning previous application builds..."
rm -rf client/node_modules client/dist
rm -rf server/OpenScanner.Server/bin server/OpenScanner.Server/obj

# 1. Run Installer
log_info "Step 1: Running install_service.sh..."
# Run as current user!
if "$SCRIPT_DIR/install_service.sh"; then
    log_success "Installer script finished successfully."
else
    log_error "Installer script failed."
    exit 1
fi

# 2. Verify Service Health
log_info "Step 2: Verifying service health..."
sleep 10 # Give the service a moment to warm up

if systemctl is-active --quiet openscanner; then
    log_success "Service 'openscanner' is active."
else
    log_error "Service 'openscanner' is NOT active."
    systemctl status openscanner
    exit 1
fi

# 3. Simple HTTP Check (Localhost Port 80)
log_info "Step 3: Checking HTTP response on port 80..."
if command -v curl &> /dev/null; then
    HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:80/ || true)
    # Expect 200 (OK) or 302 (Redirect) or maybe 404 if index not found but server up.
    if [ "$HTTP_CODE" -ne "000" ]; then
         log_success "Server responded with HTTP $HTTP_CODE."
    else
         log_error "Server did not respond on port 80 (HTTP $HTTP_CODE)."
         # We won't exit here, just log error, so we can proceed to teardown
    fi
else
    log_info "curl not found, skipping HTTP check."
fi

# 4. Teardown
log_info "Step 4: Tearing down (running uninstall_service.sh)..."
# Uninstaller now handles sudo internally
if "$SCRIPT_DIR/uninstall_service.sh"; then
    log_success "Uninstaller script finished successfully."
else
    log_error "Uninstaller script failed."
    exit 1
fi

# 5. Verify Teardown
if systemctl is-active --quiet openscanner; then
    log_error "Service 'openscanner' is STILL active after uninstall."
    exit 1
else
    log_success "Service 'openscanner' is successfully stopped."
fi

echo ""
echo "========================================="
log_success "End-to-End Installer Test Complete!"
echo "========================================="
