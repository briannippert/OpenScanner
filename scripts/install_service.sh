#!/bin/bash
set -e # Exit immediately if a command exits with a non-zero status

# ==========================================
# OpenScanner Service Installer (User Mode)
# ==========================================

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color
BOLD='\033[1m'

log_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
log_step() { echo -e "\n${BLUE}${BOLD}==> $1${NC}"; }
log_success() { echo -e "${GREEN}[OK] $1${NC}"; }
log_warn() { echo -e "${YELLOW}[WARN] $1${NC}"; }
log_error() { echo -e "${RED}[ERROR] $1${NC}"; }

# Preserve the original invocation args so we can re-exec the script verbatim
# if it updates itself during the git pull below.
INSTALLER_ARGS=("$@")

# Parse Arguments
DEPS_ONLY=false
for arg in "$@"; do
  case $arg in
    --deps-only)
      DEPS_ONLY=true
      shift
      ;;
  esac
done

# Check NOT Root
if [ "$EUID" -eq 0 ]; then
  log_error "Please run as a regular user (NOT root)."
  log_error "The script will ask for sudo password when needed."
  exit 1
fi

# Ensure sudo is available
if ! command -v sudo &> /dev/null; then
    log_error "This script requires 'sudo' to install system dependencies."
    exit 1
fi

# Refresh sudo credentials upfront
sudo -v

PROJECT_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)

log_step "Initializing Setup"
log_info "Project Root: $PROJECT_ROOT"
log_info "User: $USER ($HOME)"

# ----------------------------------------------------------------
# 1. Environment & Cleanup
# ----------------------------------------------------------------
log_step "Stopping existing service..."
if systemctl is-active --quiet openscanner; then
    sudo systemctl stop openscanner
    log_info "Service stopped."
else
    log_info "Service was not running."
fi

# ----------------------------------------------------------------
# 2. Update Code
# ----------------------------------------------------------------
log_step "Updating Repository..."
if git remote get-url origin &> /dev/null; then
    # Preserve user config stored in tracked files (the PowerDMS department in
    # appsettings.json) so the hard reset below can't lose it. Exported so it also
    # survives the self-restart re-exec further down. Only capture it once —
    # after a reset the file holds the committed (empty) value.
    SAVED_POWERDMS_DEPT="${SAVED_POWERDMS_DEPT:-$(jq -r '.PowerDMS.Department // ""' "$APPSETTINGS" 2>/dev/null || echo "")}"
    export SAVED_POWERDMS_DEPT

    # Local churn — the .NET build rewrites client/package*.json and the installer
    # writes the PowerDMS dept into appsettings.json, all tracked — can block a
    # fast-forward `git pull`. Discard local changes so the update always applies;
    # the preserved config is re-applied in the PowerDMS step below.
    git reset --hard HEAD 2>/dev/null || true

    # Record the installer's committed version before the pull so we can tell
    # whether the update changed the installer itself.
    PRE_PULL_INSTALLER=$(git rev-parse "HEAD:scripts/install_service.sh" 2>/dev/null || echo "")

    if git pull origin main; then
        log_success "Code pulled successfully."

        # If the pull updated this installer, re-exec the new version once so the
        # rest of the setup runs from the latest script. The guard env var stops
        # this from looping.
        POST_PULL_INSTALLER=$(git rev-parse "HEAD:scripts/install_service.sh" 2>/dev/null || echo "")
        if [ -n "$PRE_PULL_INSTALLER" ] && [ "$PRE_PULL_INSTALLER" != "$POST_PULL_INSTALLER" ] \
           && [ -z "$OPENSCANNER_INSTALLER_RELOADED" ]; then
            log_warn "Installer was updated by the pull — restarting with the new version..."
            export OPENSCANNER_INSTALLER_RELOADED=1
            exec bash "$PROJECT_ROOT/scripts/install_service.sh" "${INSTALLER_ARGS[@]}"
        fi
    else
        log_warn "Git pull failed. Continuing with local files..."
    fi
else
    log_warn "No git remote found. Skipping update."
fi

# ----------------------------------------------------------------
# 3. Dependency Checks (Node.js for .NET build, .NET SDK)
# ----------------------------------------------------------------
log_step "Checking Environment..."

# --- Check .NET SDK ---
INSTALL_DOTNET=false
if ! command -v dotnet &> /dev/null; then
    log_info ".NET SDK not found."
    INSTALL_DOTNET=true
else
    DOTNET_VER=$(dotnet --version)
    # Check if major version is less than 10
    MAJOR_VER=$(echo "$DOTNET_VER" | cut -d. -f1)
    if [ "$MAJOR_VER" -lt 10 ]; then
        log_info "Found .NET SDK $DOTNET_VER, but .NET 10 is required."
        INSTALL_DOTNET=true
    else
        log_success "Found .NET SDK: $DOTNET_VER"
    fi
fi

if [ "$INSTALL_DOTNET" = true ]; then
    log_info "Attempting to install .NET 10 SDK..."
    
    # Simple check for apt/debian based systems
    if command -v apt-get &> /dev/null; then
        # Add Microsoft repository (Standard robust method for Debian/Ubuntu)
        wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
        sudo dpkg -i packages-microsoft-prod.deb
        rm packages-microsoft-prod.deb
        
        sudo apt-get update -qq || log_warn "apt-get update encountered errors. Attempting to continue..."
        sudo apt-get install -y -qq dotnet-sdk-10.0
        log_success ".NET 10 SDK installed."
    else
         log_warn "Could not install .NET SDK automatically. Please install .NET 10 SDK manually."
    fi
fi

# --- Check NBGV Tool ---
# Add .dotnet/tools to PATH first, as it might already be installed there
export PATH="$PATH:$HOME/.dotnet/tools"

if ! command -v nbgv &> /dev/null; then
    log_info "Installing nbgv versioning tool..."
    dotnet tool install -g nbgv
fi

# --- Check Node.js (Required for .NET client build step) ---
NODE_PATH=$(which node || true)

if [ -z "$NODE_PATH" ]; then
    log_warn "Node.js not found! Attempting to install via NVM..."

    # Ensure curl is present
    if ! command -v curl &> /dev/null; then
        if command -v apt-get &> /dev/null; then
             log_info "Installing curl..."
             sudo apt-get update -qq && sudo apt-get install -y -qq curl
        else
             log_error "curl is required but not found. Please install curl."
             exit 1
        fi
    fi

    # Download and install nvm
    if [ ! -d "$HOME/.nvm" ]; then
        log_info "Installing nvm..."
        curl -o- https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.3/install.sh | bash
    fi

    # Load nvm
    export NVM_DIR="$HOME/.nvm"
    [ -s "$NVM_DIR/nvm.sh" ] && \. "$NVM_DIR/nvm.sh"

    # Install Node.js 24
    log_info "Installing Node.js 24..."
    nvm install 24

    NODE_PATH=$(which node || true)
    if [ -n "$NODE_PATH" ]; then
        log_success "Node.js installed successfully: $NODE_PATH"
        log_info "Node version: $(node -v)"
        log_info "NPM version: $(npm -v)"
    else
        log_error "Failed to install Node.js via NVM."
    fi
else
    log_success "Found Node: $NODE_PATH"
fi

# ----------------------------------------------------------------
# 4. System Dependencies
# ----------------------------------------------------------------
log_step "Installing System Libraries..."

sudo apt-get update -qq || log_warn "apt-get update encountered errors. Attempting to continue..."
sudo apt-get install -y -qq git cmake build-essential \
    libitpp-dev libsndfile1-dev libusb-1.0-0-dev libncurses-dev \
    rtl-sdr librtlsdr-dev libcodec2-dev libpulse-dev libasound2-dev \
    gpsd gpsd-clients ffmpeg multimon-ng openssl > /dev/null
log_success "Libraries installed."

# --- Whisper.cpp Setup ---
log_step "Checking Whisper.cpp..."
if [ ! -d "$PROJECT_ROOT/whisper.cpp" ]; then
    log_info "Cloning whisper.cpp..."
    git clone https://github.com/ggerganov/whisper.cpp.git "$PROJECT_ROOT/whisper.cpp"
fi

if [ ! -f "$PROJECT_ROOT/whisper.cpp/build/bin/whisper-cli" ]; then
    log_info "Building whisper.cpp..."
    cd "$PROJECT_ROOT/whisper.cpp"
    # Using cmake for consistent build path expected by server
    cmake -B build
    cmake --build build --config Release -j$(nproc)
    log_success "Whisper.cpp built."
fi

# Get model from appsettings.json
WHISPER_MODEL=$(grep '"Model":' "$PROJECT_ROOT/server/OpenScanner.Server/appsettings.json" | head -n 1 | cut -d'"' -f4 || echo "small.en")
log_info "Transcription Model: $WHISPER_MODEL"

if [ ! -f "$PROJECT_ROOT/whisper.cpp/models/ggml-$WHISPER_MODEL.bin" ]; then
    log_info "Downloading Whisper model ($WHISPER_MODEL)..."
    cd "$PROJECT_ROOT/whisper.cpp"
    # download script takes the name without ggml- and .bin (e.g. small.en)
    bash ./models/download-ggml-model.sh "$WHISPER_MODEL"
    log_success "Whisper model downloaded."
fi
cd "$PROJECT_ROOT"

# Configure GPSD
log_step "Configuring GPSD..."
if [ ! -f /etc/default/gpsd ]; then
    sudo bash -c 'cat <<EOF > /etc/default/gpsd
START_DAEMON="true"
USBAUTO="true"
DEVICES="/dev/ttyACM0"
GPSD_OPTIONS="-n"
GPSD_SOCKET="/var/run/gpsd.sock"
EOF'
    sudo systemctl restart gpsd
fi

# Check Hardware Drivers & Install dsd-fme
log_step "Checking Radio Dependencies..."

if ! command -v dsd-fme &> /dev/null || ! ldconfig -p | grep -q libmbe; then
    log_info "dsd-fme or mbelib not found. Installing..."
    
    mkdir -p build_deps
    cd build_deps

    # mbelib
    # Check for library in cache AND header file presence to ensure valid dev environment
    if ! ldconfig -p | grep -q libmbe || { [ ! -f "/usr/local/include/mbelib.h" ] && [ ! -f "/usr/include/mbelib.h" ]; }; then
        log_info "Building mbelib..."
        if [ ! -d "mbelib" ]; then
            git clone https://github.com/szechyjs/mbelib.git
        fi
        cd mbelib
        rm -rf build
        mkdir -p build && cd build
        # mbelib/dsd-fme ship an ancient cmake_minimum_required (<3.5), which
        # CMake 4+ (Ubuntu 26.04) refuses to configure. Allow the old policy.
        cmake -DCMAKE_POLICY_VERSION_MINIMUM=3.5 ..
        make -j$(nproc)
        sudo make install
        sudo ldconfig
        cd ../..
    fi

    # dsd-fme
    if ! command -v dsd-fme &> /dev/null; then
        log_info "Building dsd-fme..."
        if [ ! -d "dsd-fme" ]; then
            git clone https://github.com/lwvmobile/dsd-fme.git
        fi
        cd dsd-fme
        git pull origin $(git branch --show-current) || true
        rm -rf build
        mkdir -p build && cd build
        cmake -DCMAKE_POLICY_VERSION_MINIMUM=3.5 ..
        make -j$(nproc)
        sudo make install
        sudo ldconfig
        cd ../..
    fi
    cd ..
    log_success "Radio dependencies installed."
else
    log_success "Radio dependencies found."
fi

if [ "$DEPS_ONLY" = true ]; then
    log_success "Dependencies installed. Skipping build and service installation (--deps-only)."
    exit 0
fi

# ----------------------------------------------------------------
# 5. Build Application
# ----------------------------------------------------------------
log_step "Building Application..."

# Build Server (.NET) — the .NET build also compiles the React client
log_info "Building Server (.NET)..."
cd "$PROJECT_ROOT/server/OpenScanner.Server"
if dotnet build -c Release -o bin/Release/net10.0/publish; then
    log_success "Server built successfully."
else
    log_error "Server build failed."
    exit 1
fi

# ----------------------------------------------------------------
# 5a. PowerDMS Integration (Optional)
# ----------------------------------------------------------------
log_step "PowerDMS Integration..."

APPSETTINGS="$PROJECT_ROOT/server/OpenScanner.Server/appsettings.json"

# Ensure jq is available for JSON editing
if ! command -v jq &> /dev/null; then
    log_info "Installing jq for JSON configuration..."
    if command -v apt-get &> /dev/null; then
        sudo apt-get install -y -qq jq
        log_success "jq installed."
    else
        log_warn "Could not install jq automatically. Skipping PowerDMS configuration."
        SKIP_POWERDMS=true
    fi
fi

if [ "${SKIP_POWERDMS:-false}" = false ]; then
    CURRENT_DEPT=$(jq -r '.PowerDMS.Department // ""' "$APPSETTINGS" 2>/dev/null || echo "")
    # Re-apply the department preserved before the update reset the config.
    if [ -z "$CURRENT_DEPT" ] && [ -n "${SAVED_POWERDMS_DEPT:-}" ]; then
        UPDATED=$(jq --arg dept "$SAVED_POWERDMS_DEPT" '.PowerDMS.Department = $dept' "$APPSETTINGS")
        echo "$UPDATED" > "$APPSETTINGS"
        CURRENT_DEPT="$SAVED_POWERDMS_DEPT"
        log_info "Restored PowerDMS department after update: $SAVED_POWERDMS_DEPT"
    fi
    if [ -z "$CURRENT_DEPT" ]; then
        echo ""
        read -r -p "$(echo -e "${BLUE}[INFO]${NC} Enter your PowerDMS department slug (leave empty to skip): ")" POWERDMS_DEPT
        if [ -n "$POWERDMS_DEPT" ]; then
            UPDATED=$(jq --arg dept "$POWERDMS_DEPT" '.PowerDMS.Department = $dept' "$APPSETTINGS")
            echo "$UPDATED" > "$APPSETTINGS"
            log_success "PowerDMS integration enabled for department: $POWERDMS_DEPT"
        else
            log_info "No PowerDMS department entered. Skipping PowerDMS configuration."
        fi
    else
        log_info "PowerDMS integration already configured for department: $CURRENT_DEPT"
    fi
fi


# ----------------------------------------------------------------
# HTTPS self-signed certificate
# ----------------------------------------------------------------
# Serving over HTTPS gives the browser a "secure context", which unlocks
# AudioWorklet (smoother live audio) and other secure-only web APIs. The cert is
# self-signed, so browsers show a one-time warning to accept — that's expected
# for a LAN device. Kept outside git (see .gitignore) so the pre-pull hard reset
# can't delete it, and reused across installs.
log_step "Configuring HTTPS certificate..."
CERT_DIR="$PROJECT_ROOT/certs"
CERT_PFX="$CERT_DIR/openscanner.pfx"
CERT_PASSWORD="openscanner"
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
        log_warn "Certificate generation failed — the service will run HTTP-only."
        CERT_PFX=""
    fi
fi

log_step "Configuring Systemd Service..."
SERVICE_FILE="/etc/systemd/system/openscanner.service"
NET_EXEC="$PROJECT_ROOT/server/OpenScanner.Server/bin/Release/net10.0/publish/OpenScanner.Server"

# Ensure executable permission
chmod +x "$NET_EXEC"

# Bind HTTPS (443) alongside HTTP (80) when a certificate is available, and point
# Kestrel at the self-signed PFX via environment variables (no server code needed).
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

# Create temp file for service config
TEMP_SERVICE_FILE=$(mktemp)
cat <<EOF > "$TEMP_SERVICE_FILE"
[Unit]
Description=OpenScanner Radio Service (.NET)
After=network.target sound.target

[Service]
Type=simple
User=root
WorkingDirectory=$PROJECT_ROOT/server/OpenScanner.Server
ExecStart=$NET_EXEC --urls "$BIND_URLS"
Restart=always
RestartSec=5
Environment=DOTNET_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=$BIND_URLS
$CERT_ENV

[Install]
WantedBy=multi-user.target
EOF

# Move service file to correct location with sudo
sudo mv "$TEMP_SERVICE_FILE" "$SERVICE_FILE"
sudo chown root:root "$SERVICE_FILE"
sudo chmod 644 "$SERVICE_FILE"

sudo systemctl daemon-reload
sudo systemctl enable openscanner
sudo systemctl restart openscanner

# ----------------------------------------------------------------
# 7. Finalize
# ----------------------------------------------------------------
log_step "Finalizing Permissions..."
# Restore executable bits for scripts
chmod +x "$PROJECT_ROOT"/scripts/*.sh

IP_ADDR=$(hostname -I | awk '{print $1}')

echo ""
echo "================================================="
log_success "Installation Complete!"
echo "================================================="
echo -e "   ${BOLD}Status:${NC} systemctl status openscanner"
echo -e "   ${BOLD}Logs:${NC}   journalctl -u openscanner -f"
echo -e "   ${BOLD}Web UI:${NC} http://$IP_ADDR"
if [ -n "$CERT_PFX" ]; then
    echo -e "   ${BOLD}Secure:${NC} https://$IP_ADDR  (self-signed — accept the browser warning; enables smoother live audio)"
fi
echo "================================================="
