#!/bin/bash

# OpenScanner Startup Script

# Kill background processes on exit
trap "kill 0" EXIT

echo "🚀 Starting OpenScanner P25..."

# Check if SIMULATION mode is requested
REAL_MODE=true
if [[ "$1" == "--sim" ]]; then
    REAL_MODE=false
    echo "🧪 Simulation Mode Enabled"
else
    echo "📻 Hardware Mode Enabled (RTLSDRv3)"
    
    # Check for rtl_sdr tools
    if ! command -v rtl_power &> /dev/null; then
        echo "❌ Error: 'rtl_power' not found. Please run 'sudo apt install rtl-sdr' first."
        exit 1
    fi
fi

# Build Frontend
echo "🔨 Building Frontend..."
cd client
npm run build
if [ $? -ne 0 ]; then
    echo "❌ Frontend build failed! Stopping."
    exit 1
fi
cd ..

# Start Backend
echo "📡 Starting Backend Server (.NET)..."
cd server-net/OpenScanner.Server
dotnet run -c Release --urls "http://0.0.0.0:80" &
SERVER_PID=$!

HOSTNAME=$(hostname)
IP_ADDR=$(hostname -I | awk '{print $1}')
echo "------------------------------------------------"
echo "✅ OpenScanner is initializing!"
echo "Dashboard: http://$IP_ADDR"
echo "API Docs:  http://$IP_ADDR/swagger"
echo "Press Ctrl+C to stop the server."
echo "------------------------------------------------"

# Keep script running
wait
