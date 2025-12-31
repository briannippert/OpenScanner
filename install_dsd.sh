#!/bin/bash
set -e

echo "📦 Installing dependencies for OpenScanner (DSD-FME & mbelib)..."

# 1. System Dependencies
echo "--> Updating apt repositories and installing libraries..."
sudo apt-get update
sudo apt-get install -y git cmake build-essential \
    libitpp-dev libsndfile1-dev libusb-1.0-0-dev libncurses-dev \
    rtl-sdr librtlsdr-dev libcodec2-dev libpulse-dev libasound2-dev

# Create a temporary directory for building
mkdir -p build_deps
cd build_deps

# 2. Build & Install mbelib (Required for P25 decoding)
# Checks if library exists in cache to avoid rebuilding
if ! ldconfig -p | grep -q libmbe; then
    echo "--> 🎤 Building mbelib (P25 codec)..."
    if [ ! -d "mbelib" ]; then
        git clone https://github.com/szechyjs/mbelib.git
    fi
    cd mbelib
    # Clean previous builds
    rm -rf build
    mkdir -p build && cd build
    cmake ..
    make -j$(nproc)
    echo "--> Installing mbelib..."
    sudo make install
    sudo ldconfig
    cd ../..
else
    echo "✅ mbelib already installed."
fi

# 3. Build & Install dsd-fme
echo "--> 📻 Building dsd-fme..."
if [ ! -d "dsd-fme" ]; then
    git clone https://github.com/lwvmobile/dsd-fme.git
fi
cd dsd-fme
# Pull latest changes if it already exists
git pull origin master || true

# Clean previous builds
rm -rf build
mkdir -p build && cd build
cmake ..
make -j$(nproc)
echo "--> Installing dsd-fme..."
sudo make install
sudo ldconfig
cd ../..

# Cleanup
cd ..
# rm -rf build_deps # Optional: Keep for debugging

echo "------------------------------------------------"
echo "🎉 Installation complete!"
echo "You can verify installation by running: dsd-fme -h"
echo "------------------------------------------------"
