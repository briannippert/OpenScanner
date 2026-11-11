#!/bin/bash
# ==========================================
# OpenScanner installer (Linux / Raspberry Pi)
# ==========================================
# Installs OpenScanner from a prebuilt release — no repo clone, .NET SDK or Node
# needed on the device:
#
#   curl -fsSL https://github.com/briannippert/OpenScanner/releases/latest/download/install.sh | sudo bash
#
# Re-running it repairs the install and moves it to the latest release. An older
# install that was built from a git clone is detected and migrated automatically.
#
# Options:
#   --version <tag>        install a specific release (default: latest)
#   --tarball <path>       install from a local release tarball instead of downloading
#   --migrate-from <dir>   move data/certs/models out of an old git-clone install
#   --deps-only            install system + radio dependencies, then stop
#   --no-model             do not pre-download the Whisper model (the server fetches it on first use)
#
# Layout under /opt/openscanner (override with OPENSCANNER_HOME):
#   app/        the release; replaced wholesale by every update
#   app.prev/   the previous release, kept for rollback
#   data/       database, recordings, samples          (kept across updates)
#   certs/      self-signed HTTPS certificate          (kept across updates)
#   whisper/    models, and a device-built whisper-cli if one was needed
#   config/     appsettings.json overrides             (kept across updates)
#   build_deps/ mbelib + dsd-fme sources (built here: mbelib carries a patent
#               notice, so no binary of it is shipped)
set -euo pipefail

REPO_SLUG="briannippert/OpenScanner"
HOME_DIR="${OPENSCANNER_HOME:-/opt/openscanner}"
UNIT="openscanner"
SERVICE_FILE="/etc/systemd/system/$UNIT.service"
CERT_PASSWORD="openscanner"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; BLUE='\033[0;34m'; NC='\033[0m'; BOLD='\033[1m'
log_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
log_step() { echo -e "\n${BLUE}${BOLD}==> $1${NC}"; }
log_success() { echo -e "${GREEN}[OK] $1${NC}"; }
log_warn() { echo -e "${YELLOW}[WARN] $1${NC}"; }
log_error() { echo -e "${RED}[ERROR] $1${NC}" >&2; }

# ----------------------------------------------------------------
# Arguments
# ----------------------------------------------------------------
VERSION="latest"
TARBALL=""
LEGACY_ROOT=""
DEPS_ONLY=false
FETCH_MODEL=true
ORIGINAL_ARGS=("$@")
while [ $# -gt 0 ]; do
    case "$1" in
        --version) VERSION="${2:?--version needs a tag}"; shift 2 ;;
        --tarball) TARBALL="${2:?--tarball needs a path}"; shift 2 ;;
        --migrate-from) LEGACY_ROOT="${2:?--migrate-from needs a directory}"; shift 2 ;;
        --deps-only) DEPS_ONLY=true; shift ;;
        --no-model) FETCH_MODEL=false; shift ;;
        -h|--help) sed -n '2,29p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) log_error "Unknown option: $1"; exit 2 ;;
    esac
done

if [ "$EUID" -ne 0 ]; then
    # Started from a file: re-run under sudo. Piped from curl there is no file to re-run.
    if [ -f "${BASH_SOURCE[0]:-}" ] && command -v sudo > /dev/null; then
        exec sudo OPENSCANNER_HOME="$HOME_DIR" bash "${BASH_SOURCE[0]}" "${ORIGINAL_ARGS[@]}"
    fi
    log_error "This installer needs root. Run it as:  curl -fsSL https://github.com/$REPO_SLUG/releases/latest/download/install.sh | sudo bash"
    exit 1
fi

case "$(uname -m)" in
    aarch64|arm64) ARCH="arm64" ;;
    x86_64|amd64) ARCH="x64" ;;
    *) log_error "Unsupported CPU architecture '$(uname -m)'. Releases are built for 64-bit ARM and x86-64."; exit 1 ;;
esac
ASSET="openscanner-linux-$ARCH.tar.gz"

if ! command -v apt-get > /dev/null; then
    log_error "This installer supports Debian-based systems (Raspberry Pi OS, Ubuntu, Debian)."
    exit 1
fi
if [ "$DEPS_ONLY" = false ] && ! command -v systemctl > /dev/null; then
    log_error "systemd is required to run OpenScanner as a service."
    exit 1
fi

[ -z "$TARBALL" ] || TARBALL=$(readlink -f "$TARBALL")
[ -z "$LEGACY_ROOT" ] || LEGACY_ROOT=$(readlink -f "$LEGACY_ROOT")

log_step "Initializing"
log_info "Install root: $HOME_DIR"
log_info "Architecture: linux-$ARCH"

# An install made by the old git-clone installer has a unit that runs out of the
# checkout. Find that checkout so its data can be carried over.
if [ -z "$LEGACY_ROOT" ] && [ -f "$SERVICE_FILE" ] && ! grep -q 'OPENSCANNER_HOME=' "$SERVICE_FILE"; then
    LEGACY_WD=$(sed -n 's/^WorkingDirectory=//p' "$SERVICE_FILE" | head -n1)
    case "$LEGACY_WD" in
        */server/OpenScanner.Server) LEGACY_ROOT="${LEGACY_WD%/server/OpenScanner.Server}" ;;
    esac
fi
if [ -n "$LEGACY_ROOT" ]; then
    if [ -d "$LEGACY_ROOT" ] && [ "$LEGACY_ROOT" != "$HOME_DIR" ]; then
        log_info "Existing git-clone install found at $LEGACY_ROOT — it will be migrated."
    else
        LEGACY_ROOT=""
    fi
fi

mkdir -p "$HOME_DIR"

# ----------------------------------------------------------------
# 1. System dependencies
# ----------------------------------------------------------------
log_step "Installing system libraries..."
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq || log_warn "apt-get update reported errors; continuing."
# libicu's package name carries its version (libicu72, libicu74, ...); the
# self-contained .NET runtime needs whichever one this distro ships.
ICU_PKG=$(apt-cache pkgnames libicu | grep -E '^libicu[0-9]+$' | sort -V | tail -n1 || true)
# shellcheck disable=SC2086
apt-get install -y -qq curl ca-certificates tar jq openssl ffmpeg rtl-sdr multimon-ng \
    gpsd gpsd-clients libgomp1 $ICU_PKG > /dev/null
log_success "Libraries installed."

log_step "Configuring GPSD..."
if [ ! -f /etc/default/gpsd ]; then
    cat > /etc/default/gpsd <<'GPSD'
START_DAEMON="true"
USBAUTO="true"
DEVICES="/dev/ttyACM0"
GPSD_OPTIONS="-n"
GPSD_SOCKET="/var/run/gpsd.sock"
GPSD
    systemctl restart gpsd 2>/dev/null || true
    log_success "GPSD configured."
else
    log_info "GPSD already configured."
fi

install_build_tools() {
    apt-get install -y -qq git cmake build-essential > /dev/null
}

# ----------------------------------------------------------------
# 2. Radio decoders (mbelib + dsd-fme), built from source on the device
# ----------------------------------------------------------------
log_step "Checking radio decoders..."
# No `grep -q` on a pipe here: under pipefail it fails when grep matches early and
# closes the pipe on the writer, which would read as "mbelib is missing".
has_mbelib() { ldconfig -p | grep libmbe > /dev/null; }

if ! command -v dsd-fme > /dev/null || ! has_mbelib; then
    log_info "Building mbelib and dsd-fme from source (one-time, a few minutes)..."
    install_build_tools
    apt-get install -y -qq libitpp-dev libsndfile1-dev libusb-1.0-0-dev libncurses-dev \
        librtlsdr-dev libcodec2-dev libpulse-dev libasound2-dev > /dev/null
    mkdir -p "$HOME_DIR/build_deps"

    if ! has_mbelib || { [ ! -f /usr/local/include/mbelib.h ] && [ ! -f /usr/include/mbelib.h ]; }; then
        log_info "Building mbelib..."
        [ -d "$HOME_DIR/build_deps/mbelib" ] || git clone --quiet https://github.com/szechyjs/mbelib.git "$HOME_DIR/build_deps/mbelib"
        rm -rf "$HOME_DIR/build_deps/mbelib/build"
        # mbelib/dsd-fme declare an ancient cmake_minimum_required that CMake 4+
        # refuses to configure; allow the old policy.
        cmake -S "$HOME_DIR/build_deps/mbelib" -B "$HOME_DIR/build_deps/mbelib/build" -DCMAKE_POLICY_VERSION_MINIMUM=3.5 > /dev/null
        make -C "$HOME_DIR/build_deps/mbelib/build" -j"$(nproc)" > /dev/null
        make -C "$HOME_DIR/build_deps/mbelib/build" install > /dev/null
        ldconfig
    fi

    if ! command -v dsd-fme > /dev/null; then
        log_info "Building dsd-fme..."
        [ -d "$HOME_DIR/build_deps/dsd-fme" ] || git clone --quiet https://github.com/lwvmobile/dsd-fme.git "$HOME_DIR/build_deps/dsd-fme"
        git -C "$HOME_DIR/build_deps/dsd-fme" pull --quiet || true
        rm -rf "$HOME_DIR/build_deps/dsd-fme/build"
        cmake -S "$HOME_DIR/build_deps/dsd-fme" -B "$HOME_DIR/build_deps/dsd-fme/build" -DCMAKE_POLICY_VERSION_MINIMUM=3.5 > /dev/null
        make -C "$HOME_DIR/build_deps/dsd-fme/build" -j"$(nproc)" > /dev/null
        make -C "$HOME_DIR/build_deps/dsd-fme/build" install > /dev/null
        ldconfig
    fi
    log_success "Radio decoders installed."
else
    log_success "Radio decoders found."
fi

if [ "$DEPS_ONLY" = true ]; then
    log_success "Dependencies installed. Skipping the application (--deps-only)."
    exit 0
fi

# ----------------------------------------------------------------
# 3. Fetch and unpack the release
# ----------------------------------------------------------------
STAGING="$HOME_DIR/staging"
NEW="$STAGING/openscanner"
rm -rf "$STAGING"
mkdir -p "$STAGING"

if [ -n "$TARBALL" ]; then
    log_step "Using local release $TARBALL"
    [ -f "$TARBALL" ] || { log_error "Tarball not found: $TARBALL"; exit 1; }
else
    if [ "$VERSION" = "latest" ]; then
        BASE_URL="https://github.com/$REPO_SLUG/releases/latest/download"
    else
        BASE_URL="https://github.com/$REPO_SLUG/releases/download/$VERSION"
    fi
    log_step "Downloading release ($VERSION)..."
    TARBALL="$STAGING/$ASSET"
    curl -fL --retry 3 --progress-bar -o "$TARBALL" "$BASE_URL/$ASSET" \
        || { log_error "Could not download $BASE_URL/$ASSET"; exit 1; }
    curl -fsSL --retry 3 -o "$STAGING/SHA256SUMS" "$BASE_URL/SHA256SUMS" \
        || { log_error "Could not download $BASE_URL/SHA256SUMS"; exit 1; }

    EXPECTED=$(awk -v f="$ASSET" '{ n = $2; sub(/^\*/, "", n); if (n == f) print $1 }' "$STAGING/SHA256SUMS" | head -n1)
    ACTUAL=$(sha256sum "$TARBALL" | awk '{print $1}')
    if [ -z "$EXPECTED" ] || [ "$EXPECTED" != "$ACTUAL" ]; then
        log_error "Checksum mismatch for $ASSET (expected '${EXPECTED:-none listed}', got $ACTUAL)."
        exit 1
    fi
    log_success "Checksum verified."
fi

tar -xzf "$TARBALL" -C "$STAGING"
[ -x "$NEW/server/OpenScanner.Server" ] || { log_error "The release archive is missing server/OpenScanner.Server."; exit 1; }
RELEASE_VERSION=$(cat "$NEW/VERSION" 2>/dev/null || echo "unknown")
log_info "Release version: $RELEASE_VERSION"

# ----------------------------------------------------------------
# 4. Whisper (transcription)
# ----------------------------------------------------------------
log_step "Checking Whisper..."
mkdir -p "$HOME_DIR/whisper/models"

# Can this CPU run the prebuilt whisper-cli? REQUIRES lists the /proc/cpuinfo
# flags it was compiled for; a missing one means an illegal-instruction crash
# mid-transcription rather than at startup, so check up front.
bundled_whisper_ok() {
    local bin="$NEW/whisper/bin/whisper-cli" flag
    [ -x "$bin" ] || return 1
    for flag in $(cat "$NEW/whisper/REQUIRES" 2>/dev/null); do
        grep -m1 -E '^(flags|Features)' /proc/cpuinfo | grep -w "$flag" > /dev/null || return 1
    done
    "$bin" --help > /dev/null 2>&1
}

# Where the model download script will be once the release is swapped into app/.
DOWNLOAD_SCRIPT="$HOME_DIR/app/whisper/download-ggml-model.sh"
if bundled_whisper_ok; then
    log_success "Using the prebuilt whisper-cli."
    # A device-built copy from an earlier install would shadow the working bundled one.
    rm -rf "$HOME_DIR/whisper/bin" "$HOME_DIR/whisper/download-ggml-model.sh"
elif [ -x "$HOME_DIR/whisper/bin/whisper-cli" ] && "$HOME_DIR/whisper/bin/whisper-cli" --help > /dev/null 2>&1; then
    log_success "Using the whisper-cli previously built on this device."
    DOWNLOAD_SCRIPT="$HOME_DIR/whisper/download-ggml-model.sh"
else
    log_warn "The prebuilt whisper-cli cannot run on this CPU; building one here (several minutes)..."
    install_build_tools
    if bash "$NEW/scripts/build_whisper.sh" "$HOME_DIR/whisper" --native; then
        log_success "whisper-cli built for this device."
        DOWNLOAD_SCRIPT="$HOME_DIR/whisper/download-ggml-model.sh"
    else
        log_warn "whisper-cli build failed — transcription will be unavailable until it is fixed."
        DOWNLOAD_SCRIPT=""
    fi
fi

# ----------------------------------------------------------------
# 5. Migrate an old git-clone install
# ----------------------------------------------------------------
# From here on the old install is being taken apart, so any failure has to put it
# back together: MOVED records "new<TAB>old" for everything that was moved.
MOVED=()
LEGACY_UNIT_BACKUP=""
SWITCHED=false

rollback_migration() {
    local status=$?
    [ "$status" -ne 0 ] && [ "$SWITCHED" = false ] || return 0
    trap - EXIT
    if [ ${#MOVED[@]} -gt 0 ] || [ -n "$LEGACY_UNIT_BACKUP" ]; then
        log_error "Install failed — restoring the previous install at $LEGACY_ROOT"
        local entry
        for entry in "${MOVED[@]}"; do
            mv "${entry%%$'\t'*}" "${entry##*$'\t'}" 2>/dev/null || log_error "Could not restore ${entry##*$'\t'}"
        done
        if [ -n "$LEGACY_UNIT_BACKUP" ] && [ -f "$LEGACY_UNIT_BACKUP" ]; then
            cp "$LEGACY_UNIT_BACKUP" "$SERVICE_FILE"
            systemctl daemon-reload
        fi
    fi
    if [ -f "$SERVICE_FILE" ]; then systemctl start "$UNIT" 2>/dev/null || true; fi
    exit "$status"
}
trap rollback_migration EXIT

# move_legacy <old> <new>: move only when there is something to move and nothing
# already at the destination, so a re-run never clobbers newer data.
move_legacy() {
    local old="$1" new="$2"
    [ -e "$old" ] || return 0
    if [ -e "$new" ] && [ -n "$(ls -A "$new" 2>/dev/null)" ]; then
        log_warn "Keeping existing $new; left $old in place."
        return 0
    fi
    rm -rf "$new"
    mkdir -p "$(dirname "$new")"
    mv "$old" "$new"
    MOVED+=("$new"$'\t'"$old")
    log_info "Moved $old -> $new"
}

log_step "Stopping existing service..."
if systemctl is-active --quiet "$UNIT"; then
    systemctl stop "$UNIT"
    log_info "Service stopped."
else
    log_info "Service was not running."
fi

if [ -n "$LEGACY_ROOT" ]; then
    log_step "Migrating data from $LEGACY_ROOT..."
    if [ -f "$SERVICE_FILE" ]; then
        LEGACY_UNIT_BACKUP="$HOME_DIR/$UNIT.service.pre-migration"
        cp "$SERVICE_FILE" "$LEGACY_UNIT_BACKUP"
    fi

    move_legacy "$LEGACY_ROOT/data" "$HOME_DIR/data"
    move_legacy "$LEGACY_ROOT/certs" "$HOME_DIR/certs"
    for model in "$LEGACY_ROOT"/whisper.cpp/models/ggml-*.bin; do
        [ -f "$model" ] || continue
        case "$(basename "$model")" in for-tests-*) continue ;; esac
        move_legacy "$model" "$HOME_DIR/whisper/models/$(basename "$model")"
    done

    # The old installer wrote the PowerDMS department into the checkout's appsettings.json.
    LEGACY_DEPT=$(jq -r '.PowerDMS.Department // ""' "$LEGACY_ROOT/server/OpenScanner.Server/appsettings.json" 2>/dev/null || true)
    if [ -n "$LEGACY_DEPT" ] && [ ! -f "$HOME_DIR/config/appsettings.json" ]; then
        mkdir -p "$HOME_DIR/config"
        jq -n --arg dept "$LEGACY_DEPT" '{PowerDMS: {Department: $dept}}' > "$HOME_DIR/config/appsettings.json"
        log_info "Carried over PowerDMS department: $LEGACY_DEPT"
    fi
    log_success "Migration staged."
fi

mkdir -p "$HOME_DIR/data" "$HOME_DIR/config"

# ----------------------------------------------------------------
# 6. HTTPS self-signed certificate
# ----------------------------------------------------------------
# HTTPS gives the browser a "secure context", which unlocks AudioWorklet (smoother
# live audio). The cert is self-signed, so browsers show a one-time warning.
log_step "Configuring HTTPS certificate..."
CERT_DIR="$HOME_DIR/certs"
CERT_PFX="$CERT_DIR/openscanner.pfx"
mkdir -p "$CERT_DIR"
if [ -f "$CERT_PFX" ]; then
    log_info "Reusing existing certificate at $CERT_PFX"
else
    LOCAL_IP=$(hostname -I 2>/dev/null | awk '{print $1}')
    SAN="DNS:localhost,DNS:$(hostname)"
    [ -n "$LOCAL_IP" ] && SAN="$SAN,IP:$LOCAL_IP"
    SAN="$SAN,IP:127.0.0.1"
    if openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
        -keyout "$CERT_DIR/key.pem" -out "$CERT_DIR/cert.pem" \
        -subj "/CN=openscanner" -addext "subjectAltName=$SAN" 2>/dev/null \
       && openssl pkcs12 -export -out "$CERT_PFX" \
        -inkey "$CERT_DIR/key.pem" -in "$CERT_DIR/cert.pem" \
        -passout "pass:$CERT_PASSWORD" 2>/dev/null; then
        rm -f "$CERT_DIR/key.pem" "$CERT_DIR/cert.pem"
        chmod 640 "$CERT_PFX"
        log_success "Generated self-signed certificate ($SAN)."
    else
        rm -f "$CERT_DIR/key.pem" "$CERT_DIR/cert.pem"
        log_warn "Certificate generation failed — the service will run HTTP-only."
        CERT_PFX=""
    fi
fi

# ----------------------------------------------------------------
# 7. systemd unit
# ----------------------------------------------------------------
log_step "Configuring systemd service..."
if [ -n "$CERT_PFX" ]; then
    BIND_URLS="http://0.0.0.0:80;https://0.0.0.0:443"
    CERT_ENV="Environment=ASPNETCORE_Kestrel__Certificates__Default__Path=$CERT_PFX
Environment=ASPNETCORE_Kestrel__Certificates__Default__Password=$CERT_PASSWORD"
    log_info "Service will listen on http://:80 and https://:443"
else
    BIND_URLS="http://0.0.0.0:80"
    CERT_ENV=""
    log_info "Service will listen on http://:80"
fi

cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=OpenScanner Radio Service (.NET)
After=network.target sound.target

[Service]
Type=simple
User=root
WorkingDirectory=$HOME_DIR/app/server
ExecStart=$HOME_DIR/app/server/OpenScanner.Server --urls "$BIND_URLS"
Restart=always
RestartSec=5
Environment=DOTNET_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=$BIND_URLS
Environment=OPENSCANNER_HOME=$HOME_DIR
$CERT_ENV

[Install]
WantedBy=multi-user.target
EOF
chmod 644 "$SERVICE_FILE"
systemctl daemon-reload
systemctl enable --quiet "$UNIT"

# ----------------------------------------------------------------
# 8. Swap the release in and start it
# ----------------------------------------------------------------
log_step "Starting OpenScanner $RELEASE_VERSION..."
WHISPER_MODEL=$(jq -r '.Transcription.Model // "small.en"' "$NEW/server/appsettings.json" 2>/dev/null || echo "small.en")
# apply_update.sh keeps the current app/ as app.prev and rolls back to it if the
# new release does not stay up.
if ! bash "$NEW/scripts/apply_update.sh" "$NEW" "$HOME_DIR" "$UNIT"; then
    log_error "OpenScanner did not start. Check: journalctl -u $UNIT -n 100"
    exit 1
fi
SWITCHED=true
rm -f "$LEGACY_UNIT_BACKUP"
log_success "Service is running."

# ----------------------------------------------------------------
# 9. Whisper model
# ----------------------------------------------------------------
# After the service is up, so a slow or failed download never blocks the install.
if [ "$FETCH_MODEL" = true ] && [ -n "$DOWNLOAD_SCRIPT" ]; then
    log_step "Checking Whisper model ($WHISPER_MODEL)..."
    if [ -f "$HOME_DIR/whisper/models/ggml-$WHISPER_MODEL.bin" ]; then
        log_success "Model already present."
    elif bash "$DOWNLOAD_SCRIPT" "$WHISPER_MODEL" "$HOME_DIR/whisper/models"; then
        log_success "Whisper model downloaded."
    else
        log_warn "Model download failed; the server will retry when transcription first runs."
    fi
fi

IP_ADDR=$(hostname -I 2>/dev/null | awk '{print $1}')
echo ""
echo "================================================="
log_success "OpenScanner $RELEASE_VERSION installed"
echo "================================================="
echo -e "   ${BOLD}Status:${NC} systemctl status $UNIT"
echo -e "   ${BOLD}Logs:${NC}   journalctl -u $UNIT -f"
echo -e "   ${BOLD}Web UI:${NC} http://$IP_ADDR"
if [ -n "$CERT_PFX" ]; then
    echo -e "   ${BOLD}Secure:${NC} https://$IP_ADDR  (self-signed — accept the browser warning; enables smoother live audio)"
fi
echo -e "   ${BOLD}Update:${NC} from the web UI, or re-run this installer"
if [ -n "$LEGACY_ROOT" ]; then
    echo ""
    log_info "Your data now lives in $HOME_DIR. The old checkout at $LEGACY_ROOT is no longer used and can be deleted."
fi
echo "================================================="
