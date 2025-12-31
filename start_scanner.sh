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

# Start Backend
echo "📡 Starting Backend Server..."
cd server
USE_REAL_RADIO=$REAL_MODE npx ts-node src/index.ts &
SERVER_PID=$!

# Start Frontend
echo "💻 Starting Frontend UI..."
cd ../client
npm run dev &
CLIENT_PID=$!

HOSTNAME=$(hostname)
echo "------------------------------------------------"
echo "✅ OpenScanner is initializing!"
echo "Backend:  http://$HOSTNAME:3001"
echo "Frontend: http://$HOSTNAME:5173"
echo "Press Ctrl+C to stop both servers."
echo "------------------------------------------------"

# Keep script running
wait
