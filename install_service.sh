#!/bin/bash

# OpenScanner Service Installer

if [ "$EUID" -ne 0 ]; then
  echo "❌ Please run as root (e.g. sudo ./install_service.sh)"
  exit 1
fi

# Get the absolute path of the project root
PROJECT_ROOT=$(pwd)

# Detect the real user (who ran sudo)
REAL_USER=${SUDO_USER:-$USER}
REAL_HOME=$(getent passwd "$REAL_USER" | cut -d: -f6)

echo "📂 Project Root: $PROJECT_ROOT"
echo "👤 Installing for user: $REAL_USER ($REAL_HOME)"

# Stop service to release any locks/ports
echo "🔄 Stopping existing service (if any)..."
systemctl stop openscanner 2>/dev/null || true

# Update Code (Only if git remote exists)
if git remote | grep -q "origin"; then
    echo "⬇️  Pulling latest code..."
    # Run git pull as the real user to use their keys/config and preserve file permissions
    sudo -u "$REAL_USER" git pull origin master
    if [ $? -ne 0 ]; then
        echo "⚠️  Git pull failed. Continuing with current files..."
    else
        echo "✅ Code updated."
    fi
else
    echo "ℹ️  No git remote 'origin' found. Skipping update."
fi

# Check for Node.js
# First check path (in case it was preserved)
NODE_PATH=$(which node)

# If not found, look for NVM in the user's home directory
if [ -z "$NODE_PATH" ]; then
    echo "🔍 Searching for Node.js in NVM directory..."
    # Find the latest version of node in .nvm/versions/node/
    # Directory structure is usually: ~/.nvm/versions/node/vX.Y.Z/bin/node
    NVM_NODE=$(find "$REAL_HOME/.nvm/versions/node" -maxdepth 3 -name node -type f 2>/dev/null | grep "/bin/node" | sort -V | tail -n 1)
    
    if [ -n "$NVM_NODE" ]; then
        NODE_PATH="$NVM_NODE"
        # We also need to add this to PATH for the rest of this script (for npm)
        export PATH="$(dirname "$NODE_PATH"):$PATH"
    fi
fi

if [ -z "$NODE_PATH" ]; then
    # Fallback checks
    if [ -f "/usr/bin/node" ]; then
        NODE_PATH="/usr/bin/node"
    elif [ -f "/usr/local/bin/node" ]; then
        NODE_PATH="/usr/local/bin/node"
    else
        echo "❌ Node.js not found. If using NVM, ensure it is installed in $REAL_HOME/.nvm"
        exit 1
    fi
fi
echo "✅ Node.js found at: $NODE_PATH"

# Check for npm
if ! command -v npm &> /dev/null; then
    echo "❌ npm not found. Please ensure npm is in the path."
    exit 1
fi

# Check for Runtime Dependencies
MISSING_DEPS=0
if ! command -v rtl_power &> /dev/null; then
    echo "⚠️  Warning: 'rtl_power' not found. Radio scanning will fail."
    MISSING_DEPS=1
fi
if ! command -v rtl_fm &> /dev/null; then
    echo "⚠️  Warning: 'rtl_fm' not found. Radio tuning will fail."
    MISSING_DEPS=1
fi
if ! command -v dsd-fme &> /dev/null; then
    echo "⚠️  Warning: 'dsd-fme' not found. P25 decoding will fail."
    MISSING_DEPS=1
fi

if [ $MISSING_DEPS -eq 1 ]; then
    echo "------------------------------------------------"
    echo "⚠️  Some hardware dependencies are missing."
    echo "   You can install them using ./install_dsd.sh"
    echo "   The service will install, but may not function correctly until they are present."
    echo "------------------------------------------------"
    sleep 3
fi

# 1. System Dependencies
echo "--> Updating apt repositories and installing libraries..."
apt-get update
apt-get install -y git cmake build-essential \
    libitpp-dev libsndfile1-dev libusb-1.0-0-dev libncurses-dev \
    rtl-sdr librtlsdr-dev libcodec2-dev libpulse-dev libasound2-dev \
    gpsd gpsd-clients

# Configure GPSD
echo "📝 Configuring GPSD..."
cat <<EOF > /etc/default/gpsd
# Default settings for the gpsd init script and the hotplug wrapper.
START_DAEMON="true"
USBAUTO="true"
DEVICES="/dev/ttyACM0"
GPSD_OPTIONS="-n"
GPSD_SOCKET="/var/run/gpsd.sock"
EOF

systemctl restart gpsd

echo "🔧 Installing Dependencies & Building..."

# Client
echo "📦 Building Client..."
cd "$PROJECT_ROOT/client" || exit 1
npm install
npm run build
if [ $? -ne 0 ]; then
    echo "❌ Client build failed."
    exit 1
fi

# Server
echo "📦 Building Server..."
cd "$PROJECT_ROOT/server" || exit 1
npm install
npm run build
if [ $? -ne 0 ]; then
    echo "❌ Server build failed."
    exit 1
fi

# Service File
SERVICE_FILE="/etc/systemd/system/openscanner.service"
echo "📝 Creating Service File: $SERVICE_FILE"

cat <<EOF > "$SERVICE_FILE"
[Unit]
Description=OpenScanner Radio Service
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory=$PROJECT_ROOT/server
ExecStart=$NODE_PATH dist/index.js
Restart=on-failure
Environment=PORT=80
Environment=USE_REAL_RADIO=true
Environment=NODE_ENV=production

[Install]
WantedBy=multi-user.target
EOF

echo "🚀 Enabling and Starting Service..."
systemctl daemon-reload
systemctl enable openscanner
systemctl restart openscanner

IP_ADDR=$(hostname -I | awk '{print $1}')

echo "------------------------------------------------"
echo "✅ OpenScanner Service Installed!"
echo "------------------------------------------------"
echo "Check status: systemctl status openscanner"
echo "View logs:    journalctl -u openscanner -f"
echo "Access at:    http://$IP_ADDR"
echo "------------------------------------------------"
