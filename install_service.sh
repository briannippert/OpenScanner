#!/bin/bash
set -e # Exit immediately if a command exits with a non-zero status

# ==========================================
# OpenScanner Service Installer (Robust)
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

# Check Root
if [ "$EUID" -ne 0 ]; then
  log_error "Please run as root (e.g., sudo ./install_service.sh)"
  exit 1
fi

# Detect Real User
REAL_USER=${SUDO_USER:-$USER}
REAL_HOME=$(getent passwd "$REAL_USER" | cut -d: -f6)
PROJECT_ROOT=$(pwd)

log_step "Initializing Setup"
log_info "Project Root: $PROJECT_ROOT"
log_info "User: $REAL_USER ($REAL_HOME)"

# ----------------------------------------------------------------
# 1. Environment & Cleanup
# ----------------------------------------------------------------
log_step "Stopping existing service..."
if systemctl is-active --quiet openscanner; then
    systemctl stop openscanner
    log_info "Service stopped."
else
    log_info "Service was not running."
fi

# ----------------------------------------------------------------
# 2. Update Code
# ----------------------------------------------------------------
log_step "Updating Repository..."
if git remote get-url origin &> /dev/null; then
    # Fix ownership before pulling to avoid permission errors
    chown -R "$REAL_USER":"$REAL_USER" "$PROJECT_ROOT/.git"
    
    if sudo -u "$REAL_USER" git pull origin main; then
        log_success "Code pulled successfully."
    else
        log_warn "Git pull failed. Continuing with local files..."
    fi
else
    log_warn "No git remote found. Skipping update."
fi

# ----------------------------------------------------------------
# 3. Dependency Checks (Node.js for Client, .NET for Server)
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
        dpkg -i packages-microsoft-prod.deb
        rm packages-microsoft-prod.deb
        
        apt-get update -qq || log_warn "apt-get update encountered errors. Attempting to continue..."
        apt-get install -y -qq dotnet-sdk-10.0
        log_success ".NET 10 SDK installed."
    else
         log_warn "Could not install .NET SDK automatically. Please install .NET 10 SDK manually."
    fi
fi

# --- Check NBGV Tool ---
if ! command -v nbgv &> /dev/null; then
    log_info "Installing nbgv versioning tool..."
    dotnet tool install -g nbgv
    export PATH="$PATH:$REAL_HOME/.dotnet/tools"
fi

# --- Check Node.js (For Client Build) ---
NODE_PATH=$(which node || true)
if [ -z "$NODE_PATH" ]; then
    log_info "Node not in root PATH. Checking user's NVM..."
    NVM_NODE=$(find "$REAL_HOME/.nvm/versions/node" -maxdepth 3 -name node -type f 2>/dev/null | grep "/bin/node" | sort -V | tail -n 1)
    if [ -n "$NVM_NODE" ]; then
        NODE_PATH="$NVM_NODE"
        export PATH="$(dirname "$NODE_PATH"):$PATH"
        log_success "Found Node via NVM: $NODE_PATH"
    fi
fi

if [ -z "$NODE_PATH" ]; then
    # Fallback checks
    for path in /usr/bin/node /usr/local/bin/node; do
        if [ -f "$path" ]; then
            NODE_PATH="$path"
            break
        fi
    done
fi

if [ -z "$NODE_PATH" ]; then
    log_warn "Node.js not found! Client build might fail."
else
    log_success "Found Node: $NODE_PATH"
fi

# ----------------------------------------------------------------
# 4. System Dependencies
# ----------------------------------------------------------------
log_step "Installing System Libraries..."

apt-get update -qq || log_warn "apt-get update encountered errors. Attempting to continue..."
apt-get install -y -qq git cmake build-essential \
    libitpp-dev libsndfile1-dev libusb-1.0-0-dev libncurses-dev \
    rtl-sdr librtlsdr-dev libcodec2-dev libpulse-dev libasound2-dev \
    gpsd gpsd-clients ffmpeg > /dev/null
log_success "Libraries installed."

# --- Whisper.cpp Setup ---
log_step "Checking Whisper.cpp..."
if [ ! -d "$PROJECT_ROOT/whisper.cpp" ]; then
    log_info "Cloning whisper.cpp..."
    sudo -u "$REAL_USER" git clone https://github.com/ggerganov/whisper.cpp.git "$PROJECT_ROOT/whisper.cpp"
fi

if [ ! -f "$PROJECT_ROOT/whisper.cpp/build/bin/whisper-cli" ]; then
    log_info "Building whisper.cpp..."
    cd "$PROJECT_ROOT/whisper.cpp"
    # Using cmake for consistent build path expected by server
    sudo -u "$REAL_USER" cmake -B build
    sudo -u "$REAL_USER" cmake --build build --config Release -j$(nproc)
    log_success "Whisper.cpp built."
fi

if [ ! -f "$PROJECT_ROOT/whisper.cpp/models/ggml-tiny.en.bin" ]; then
    log_info "Downloading Whisper model..."
    cd "$PROJECT_ROOT/whisper.cpp"
    sudo -u "$REAL_USER" bash ./models/download-ggml-model.sh tiny.en
    log_success "Whisper model downloaded."
fi
cd "$PROJECT_ROOT"

# Configure GPSD
log_step "Configuring GPSD..."
if [ ! -f /etc/default/gpsd ]; then
    cat <<EOF > /etc/default/gpsd
START_DAEMON="true"
USBAUTO="true"
DEVICES="/dev/ttyACM0"
GPSD_OPTIONS="-n"
GPSD_SOCKET="/var/run/gpsd.sock"
EOF
    systemctl restart gpsd
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
        cmake ..
        make -j$(nproc)
        make install
        ldconfig
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
        cmake ..
        make -j$(nproc)
        make install
        ldconfig
        cd ../..
    fi
    cd ..
    log_success "Radio dependencies installed."
else
    log_success "Radio dependencies found."
fi

# ----------------------------------------------------------------
# 5. Build Application
# ----------------------------------------------------------------
log_step "Building Application..."

# Build Client
log_info "Building Client..."
cd "$PROJECT_ROOT/client"
if [ -f "package-lock.json" ] && [ -n "$NODE_PATH" ]; then
    # Sync version if nbgv is available
    if command -v nbgv &> /dev/null; then
        NPM_VER=$(nbgv get-version -v NpmPackageVersion)
        npm version "$NPM_VER" --no-git-tag-version --silent
        log_info "Synced package.json to version $NPM_VER"
    fi

    npm ci --silent
    if [ $? -ne 0 ]; then npm install --silent; fi
    
    # Fix permissions
    chmod +x node_modules/.bin/* || true

    if npm run build; then
        log_success "Client built."
    else
        log_error "Client build failed."
        exit 1
    fi
else
    log_warn "Skipping Client build (Node missing or no package.json)"
fi

# Build Server (.NET)
log_info "Building Server (.NET)..."
cd "$PROJECT_ROOT/server/OpenScanner.Server"
if dotnet build -c Release -o bin/Release/net10.0/publish; then
    log_success "Server built successfully."
else
    log_error "Server build failed."
    exit 1
fi

# ----------------------------------------------------------------
# 6. Service Installation
# ----------------------------------------------------------------
log_step "Configuring Systemd Service..."
SERVICE_FILE="/etc/systemd/system/openscanner.service"
NET_EXEC="$PROJECT_ROOT/server/OpenScanner.Server/bin/Release/net10.0/publish/OpenScanner.Server"

# Ensure executable permission
chmod +x "$NET_EXEC"

cat <<EOF > "$SERVICE_FILE"
[Unit]
Description=OpenScanner Radio Service (.NET)
After=network.target sound.target

[Service]
Type=simple
User=root
WorkingDirectory=$PROJECT_ROOT/server/OpenScanner.Server
ExecStart=$NET_EXEC --urls "http://0.0.0.0:80"
Restart=always
RestartSec=5
Environment=DOTNET_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:80

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable openscanner
systemctl restart openscanner

# ----------------------------------------------------------------
# 7. Finalize
# ----------------------------------------------------------------
log_step "Finalizing Permissions..."
# Crucial: Give files back to the user so they can edit them later
chown -R "$REAL_USER":"$REAL_USER" "$PROJECT_ROOT"
# Restore executable bits for scripts
chmod +x "$PROJECT_ROOT"/*.sh

IP_ADDR=$(hostname -I | awk '{print $1}')

echo ""
echo "================================================="
log_success "Installation Complete!"
echo "================================================="
echo -e "   ${BOLD}Status:${NC} systemctl status openscanner"
echo -e "   ${BOLD}Logs:${NC}   journalctl -u openscanner -f"
echo -e "   ${BOLD}Web UI:${NC} http://$IP_ADDR"
echo "================================================="
