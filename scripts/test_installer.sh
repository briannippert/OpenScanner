#!/bin/bash
# test_installer.sh — end-to-end test of the release install / update / migrate path.
#
# Builds a release tarball from this checkout, then on THIS machine: installs it
# as the real systemd service, exercises an update and a failed-update rollback,
# uninstalls, and finally migrates a fake legacy git-clone install.
#
# It installs to /opt/openscanner, binds ports 80/443 and replaces any existing
# 'openscanner' service, so it is meant for CI runners and throwaway VMs. It
# refuses to run elsewhere unless you pass --force.
set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; BLUE='\033[0;34m'; NC='\033[0m'
log_info() { echo -e "${BLUE}[TEST INFO]${NC} $1"; }
log_success() { echo -e "${GREEN}[TEST SUCCESS] $1${NC}"; }
fail() { echo -e "${RED}[TEST ERROR] $1${NC}" >&2; sudo journalctl -u openscanner -n 40 --no-pager 2>/dev/null || true; exit 1; }

if [ "${CI:-}" != "true" ] && [ "${1:-}" != "--force" ]; then
    echo "This test installs OpenScanner system-wide and replaces any existing install." >&2
    echo "Run it on a CI runner or a throwaway VM, or pass --force." >&2
    exit 1
fi

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
HOME_DIR=/opt/openscanner
case "$(uname -m)" in
    aarch64|arm64) RID=linux-arm64 ;;
    *) RID=linux-x64 ;;
esac

WORK=$(mktemp -d)
trap 'sudo rm -rf "$WORK"' EXIT
TARBALL="$WORK/out/openscanner-$RID.tar.gz"

assert_active() { systemctl is-active --quiet openscanner || fail "$1: service is not active."; }

# Wait for the server to answer, then return the running version.
running_version() {
    local i body
    for i in $(seq 1 30); do
        if body=$(curl -fsS http://localhost:80/api/system/info 2>/dev/null); then
            echo "$body" | jq -r '.Version // .version // empty'
            return 0
        fi
        sleep 2
    done
    return 1
}

# ---------------------------------------------------------------
log_info "Step 1: Building the release tarball ($RID)..."
"$SCRIPT_DIR/build_whisper.sh" "$WORK/whisper"
"$SCRIPT_DIR/package_release.sh" "$RID" "$WORK/out" --whisper-dir "$WORK/whisper"
[ -f "$TARBALL" ] || fail "package_release.sh did not produce $TARBALL."
log_success "Built $(basename "$TARBALL")."

# ---------------------------------------------------------------
log_info "Step 2: Fresh install..."
sudo "$SCRIPT_DIR/uninstall.sh" --purge > /dev/null 2>&1 || true
sudo "$SCRIPT_DIR/install.sh" --tarball "$TARBALL" --no-model || fail "install.sh failed."

assert_active "After install"
VERSION=$(running_version) || fail "Server did not answer on port 80."
[ -n "$VERSION" ] || fail "Server did not report a version."
log_success "Service is active and reports version $VERSION."

sudo test -f "$HOME_DIR/certs/openscanner.pfx" || fail "Certificate was not generated."
HTTPS_CODE=$(curl -sk -o /dev/null -w "%{http_code}" https://localhost:443/ || true)
[ "$HTTPS_CODE" = "200" ] || fail "HTTPS returned $HTTPS_CODE (expected 200: the bundled client)."
log_success "HTTPS serves the client."

sudo test -f "$HOME_DIR/data/openscanner.db" || fail "Database was not created under $HOME_DIR/data."
sudo test -x "$HOME_DIR/app/whisper/bin/whisper-cli" || fail "whisper-cli is missing from the install."
log_success "Data lives outside the app directory."

# ---------------------------------------------------------------
log_info "Step 3: Update (same swap the in-app updater performs)..."
stage_release() {
    sudo rm -rf "$HOME_DIR/staging"
    sudo mkdir -p "$HOME_DIR/staging"
    sudo tar -xzf "$TARBALL" -C "$HOME_DIR/staging"
}
sudo touch "$HOME_DIR/data/marker"
stage_release
sudo bash "$HOME_DIR/staging/openscanner/scripts/apply_update.sh" "$HOME_DIR/staging/openscanner" "$HOME_DIR" openscanner \
    || fail "apply_update.sh failed on a good release."
assert_active "After update"
running_version > /dev/null || fail "Server did not answer after the update."
sudo test -d "$HOME_DIR/app.prev" || fail "Previous version was not kept for rollback."
sudo test -f "$HOME_DIR/data/marker" || fail "Update touched the data directory."
log_success "Update swapped in and kept data."

# ---------------------------------------------------------------
log_info "Step 4: A broken release must roll back..."
stage_release
printf '#!/bin/sh\nexit 1\n' | sudo tee "$HOME_DIR/staging/openscanner/server/OpenScanner.Server" > /dev/null
if sudo bash "$HOME_DIR/staging/openscanner/scripts/apply_update.sh" "$HOME_DIR/staging/openscanner" "$HOME_DIR" openscanner; then
    fail "apply_update.sh reported success for a release that cannot start."
fi
assert_active "After rollback"
[ "$(running_version)" = "$VERSION" ] || fail "Rollback did not restore the working version."
sudo test -d "$HOME_DIR/app.failed" || fail "Failed release was not kept for inspection."
log_success "Broken release rolled back."

# ---------------------------------------------------------------
log_info "Step 5: Uninstall keeps data..."
sudo cp "$HOME_DIR/certs/openscanner.pfx" "$WORK/openscanner.pfx"
sudo "$HOME_DIR/app/scripts/uninstall.sh" || fail "uninstall.sh failed."
systemctl is-active --quiet openscanner && fail "Service is still active after uninstall."
sudo test -d "$HOME_DIR/app" && fail "app/ survived uninstall."
sudo test -f "$HOME_DIR/data/marker" || fail "Uninstall deleted data without --purge."
sudo "$SCRIPT_DIR/uninstall.sh" --purge || fail "uninstall.sh --purge failed."
sudo test -e "$HOME_DIR" && fail "$HOME_DIR survived --purge."
log_success "Uninstall behaves."

# ---------------------------------------------------------------
log_info "Step 6: Migrating a legacy git-clone install..."
LEGACY="$WORK/legacy"
mkdir -p "$LEGACY/data/recordings" "$LEGACY/certs" "$LEGACY/whisper.cpp/models" "$LEGACY/server/OpenScanner.Server"
echo "audio" > "$LEGACY/data/recordings/rec_1_154000000.raw"
sudo cp "$WORK/openscanner.pfx" "$LEGACY/certs/openscanner.pfx"
echo "model" > "$LEGACY/whisper.cpp/models/ggml-tiny.en.bin"
echo '{"PowerDMS":{"Department":"testdept"}}' > "$LEGACY/server/OpenScanner.Server/appsettings.json"

sudo "$SCRIPT_DIR/install.sh" --tarball "$TARBALL" --no-model --migrate-from "$LEGACY" || fail "Migrating install failed."
assert_active "After migration"
running_version > /dev/null || fail "Server did not answer after migration."
sudo test -f "$HOME_DIR/data/recordings/rec_1_154000000.raw" || fail "Recordings were not migrated."
sudo test -f "$HOME_DIR/whisper/models/ggml-tiny.en.bin" || fail "Whisper models were not migrated."
sudo cmp -s "$WORK/openscanner.pfx" "$HOME_DIR/certs/openscanner.pfx" || fail "Certificate was not migrated."
[ "$(sudo jq -r '.PowerDMS.Department' "$HOME_DIR/config/appsettings.json")" = "testdept" ] || fail "PowerDMS department was not migrated."
[ -e "$LEGACY/data" ] && fail "Legacy data directory was copied instead of moved."
DEPT=$(curl -fsS http://localhost:80/api/powerdms/config 2>/dev/null || true)
echo "$DEPT" | grep -q testdept || fail "Server did not pick up the migrated config (got: $DEPT)."
log_success "Legacy install migrated."

# ---------------------------------------------------------------
log_info "Step 7: Teardown..."
sudo "$SCRIPT_DIR/uninstall.sh" --purge || fail "Final uninstall failed."

echo ""
echo "========================================="
log_success "End-to-end installer test complete!"
echo "========================================="
