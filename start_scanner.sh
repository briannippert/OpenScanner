#!/bin/bash

# OpenScanner Startup Script

# Kill background processes on exit
trap "kill 0" EXIT

echo "[START] Starting OpenScanner P25..."

# Build Frontend
echo "[BUILD] Building Frontend..."
cd client
npm run build
if [ $? -ne 0 ]; then
    echo "[ERROR] Frontend build failed! Stopping."
    exit 1
fi
cd ..

# Start Backend
echo "[SERVER] Starting Backend Server (.NET)..."
cd server-net/OpenScanner.Server
dotnet run -c Release --urls "http://0.0.0.0:80" &
SERVER_PID=$!

HOSTNAME=$(hostname)
IP_ADDR=$(hostname -I | awk '{print $1}')
echo "------------------------------------------------"
echo "[OK] OpenScanner is initializing!"
echo "Dashboard: http://$IP_ADDR"
echo "API Docs:  http://$IP_ADDR/swagger"
echo "Press Ctrl+C to stop the server."
echo "------------------------------------------------"

# Keep script running
wait