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

log_info() { echo -e "${BLUE}ℹ️  ${NC} "; }
log_step() { echo -e "\n${BLUE}${BOLD}==> ${NC}"; }
log_success() { echo -e "${GREEN}✅ ${NC}"; }
log_warn() { echo -e "${YELLOW}⚠️  ${NC}"; }
log_error() { echo -e "${RED}❌ ${NC}"; }

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
    
    if sudo -u "$REAL_USER" git pull origin master; then
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
if ! command -v dotnet &> /dev/null; then
    log_info ".NET SDK not found. Attempting to install..."
    
    # Simple check for apt/debian based systems
    if command -v apt-get &> /dev/null; then
        # Add Microsoft repository (Standard robust method for Debian/Ubuntu)
        wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
        dpkg -i packages-microsoft-prod.deb
        rm packages-microsoft-prod.deb
        
        apt-get update -qq
        apt-get install -y -qq dotnet-sdk-8.0
        log_success ".NET SDK installed."
    else
         log_warn "Could not install .NET SDK automatically. Please install .NET 8.0 SDK manually."
    fi
else
    DOTNET_VER=$(dotnet --version)
    log_success "Found .NET SDK: $DOTNET_VER"
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

apt-get update -qq
apt-get install -y -qq git cmake build-essential \
    libitpp-dev libsndfile1-dev libusb-1.0-0-dev libncurses-dev \
    rtl-sdr librtlsdr-dev libcodec2-dev libpulse-dev libasound2-dev \
    gpsd gpsd-clients > /dev/null
log_success "Libraries installed."

# Configure GPSD
log_info "Configuring GPSD..."
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

# Check Hardware Drivers
log_step "Checking Hardware Drivers..."
MISSING_DEPS=0
for cmd in rtl_power rtl_fm dsd-fme; do
    if ! command -v $cmd &> /dev/null; then
        log_warn "Missing: $cmd"
        MISSING_DEPS=1
    else
        log_success "Found: $cmd"
    fi
done

if [ $MISSING_DEPS -eq 1 ]; then
    log_warn "Some radio tools are missing. Run ./install_dsd.sh if needed."
    sleep 2
fi

# ----------------------------------------------------------------
# 5. Build Application
# ----------------------------------------------------------------
log_step "Building Application..."

# Build Client
log_info "Building Client..."
cd "$PROJECT_ROOT/client"
if [ -f "package-lock.json" ] && [ -n "$NODE_PATH" ]; then
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
cd "$PROJECT_ROOT/server-net/OpenScanner.Server"
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
NET_EXEC="$PROJECT_ROOT/server-net/OpenScanner.Server/bin/Release/net10.0/publish/OpenScanner.Server"

# Ensure executable permission
chmod +x "$NET_EXEC"

cat <<EOF > "$SERVICE_FILE"
[Unit]
Description=OpenScanner Radio Service (.NET)
After=network.target sound.target

[Service]
Type=simple
User=root
WorkingDirectory=$PROJECT_ROOT/server-net/OpenScanner.Server
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

IP_ADDR=$(hostname -I | awk '{print }')

echo ""
echo "================================================"
log_success "Installation Complete!"
echo "================================================"
echo -e "   ${BOLD}Status:${NC} systemctl status openscanner"
echo -e "   ${BOLD}Logs:${NC}   journalctl -u openscanner -f"
echo -e "   ${BOLD}Web UI:${NC} http://$IP_ADDR"
echo "================================================"