#!/bin/bash
set -e # Exit immediately if a command exits with a non-zero status

# ==========================================
# OpenScanner macOS Setup (Development / Desktop)
# ==========================================
# Installs dependencies via Homebrew, builds whisper.cpp and the DSD-FME radio
# decoder from source, builds the .NET backend (which also builds the React
# client), and prints how to run the app.
#
# macOS has no systemd, so this script does NOT install a background service.
# Use --deps-only to install dependencies and exit without building.

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

# Parse Arguments
DEPS_ONLY=false
for arg in "$@"; do
  case $arg in
    --deps-only) DEPS_ONLY=true ; shift ;;
  esac
done

if [ "$(uname)" != "Darwin" ]; then
    log_error "This script is for macOS. On Linux/Raspberry Pi use ./scripts/install_service.sh"
    exit 1
fi

PROJECT_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
NPROC=$(sysctl -n hw.ncpu)

log_step "Initializing Setup"
log_info "Project Root: $PROJECT_ROOT"
log_info "CPU cores: $NPROC"

# ----------------------------------------------------------------
# 1. Homebrew
# ----------------------------------------------------------------
log_step "Checking Homebrew..."
if ! command -v brew &> /dev/null; then
    log_error "Homebrew is required. Install it from https://brew.sh and re-run this script."
    exit 1
fi
log_success "Found Homebrew: $(brew --prefix)"

# ----------------------------------------------------------------
# 2. System Dependencies (Homebrew)
# ----------------------------------------------------------------
log_step "Installing System Libraries..."
# rtl-sdr: rtl_sdr / rtl_fm tools + driver
# ffmpeg:  audio conversion in the decode/record/transcribe pipelines
# coreutils: provides gstdbuf (the GNU stdbuf used to tune pipe buffering)
# itpp / libsndfile / portaudio / ncurses / libusb / codec2: DSD-FME deps
# pulseaudio: provides libpulse client libs that DSD-FME links against (it only
#   pipes audio via stdin/stdout in OpenScanner, but the lib is a build-time dep)
# gpsd: optional GPS support (gpsd listens on localhost:2947)
BREW_PKGS="rtl-sdr ffmpeg coreutils cmake git node jq itpp libsndfile portaudio ncurses libusb codec2 pulseaudio gpsd"
log_info "brew install $BREW_PKGS"
brew install $BREW_PKGS
log_success "Libraries installed."

# ----------------------------------------------------------------
# 3. .NET 10 SDK
# ----------------------------------------------------------------
log_step "Checking .NET SDK..."
DOTNET_OK=false
if command -v dotnet &> /dev/null; then
    MAJOR_VER=$(dotnet --version | cut -d. -f1)
    if [ "$MAJOR_VER" -ge 10 ]; then
        DOTNET_OK=true
        log_success "Found .NET SDK: $(dotnet --version)"
    fi
fi

if [ "$DOTNET_OK" = false ]; then
    log_info "Installing .NET 10 SDK to \$HOME/.dotnet via the official installer..."
    curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --channel 10.0
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$DOTNET_ROOT:$PATH"
    log_success ".NET 10 SDK installed to $DOTNET_ROOT"
    log_warn "Add this to your shell profile so 'dotnet' is always found:"
    echo '   export DOTNET_ROOT="$HOME/.dotnet"'
    echo '   export PATH="$DOTNET_ROOT:$PATH"'
fi

# nbgv (Nerdbank.GitVersioning) tool used during the build
export PATH="$PATH:$HOME/.dotnet/tools"
if ! command -v nbgv &> /dev/null; then
    log_info "Installing nbgv versioning tool..."
    dotnet tool install -g nbgv || log_warn "Could not install nbgv globally; build may still succeed."
fi

# ----------------------------------------------------------------
# 4. Whisper.cpp (AI transcription)
# ----------------------------------------------------------------
log_step "Checking Whisper.cpp..."
if [ ! -d "$PROJECT_ROOT/whisper.cpp" ]; then
    log_info "Cloning whisper.cpp..."
    git clone https://github.com/ggerganov/whisper.cpp.git "$PROJECT_ROOT/whisper.cpp"
fi

if [ ! -f "$PROJECT_ROOT/whisper.cpp/build/bin/whisper-cli" ]; then
    log_info "Building whisper.cpp..."
    cd "$PROJECT_ROOT/whisper.cpp"
    cmake -B build
    cmake --build build --config Release -j"$NPROC"
    log_success "Whisper.cpp built."
fi

WHISPER_MODEL=$(grep '"Model":' "$PROJECT_ROOT/server/OpenScanner.Server/appsettings.json" | head -n 1 | cut -d'"' -f4 || echo "small.en")
log_info "Transcription Model: $WHISPER_MODEL"
if [ ! -f "$PROJECT_ROOT/whisper.cpp/models/ggml-$WHISPER_MODEL.bin" ]; then
    log_info "Downloading Whisper model ($WHISPER_MODEL)..."
    cd "$PROJECT_ROOT/whisper.cpp"
    bash ./models/download-ggml-model.sh "$WHISPER_MODEL"
    log_success "Whisper model downloaded."
fi
cd "$PROJECT_ROOT"

# ----------------------------------------------------------------
# 5. DSD-FME + mbelib (digital voice decoding) — built from source
# ----------------------------------------------------------------
log_step "Checking Radio Dependencies (dsd-fme)..."
if ! command -v dsd-fme &> /dev/null; then
    log_info "dsd-fme not found. Building from source..."
    mkdir -p "$PROJECT_ROOT/build_deps"
    cd "$PROJECT_ROOT/build_deps"

    # mbelib
    if [ ! -f /usr/local/include/mbelib.h ] && [ ! -f /opt/homebrew/include/mbelib.h ]; then
        log_info "Building mbelib..."
        [ -d mbelib ] || git clone https://github.com/szechyjs/mbelib.git
        cd mbelib
        rm -rf build && mkdir -p build && cd build
        # mbelib targets an ancient CMake minimum that modern CMake rejects.
        cmake -DCMAKE_POLICY_VERSION_MINIMUM=3.5 ..
        make -j"$NPROC"
        sudo make install
        cd ../..
    fi

    # dsd-fme
    log_info "Building dsd-fme..."
    [ -d dsd-fme ] || git clone https://github.com/lwvmobile/dsd-fme.git
    cd dsd-fme
    git pull origin "$(git branch --show-current)" || true
    rm -rf build && mkdir -p build && cd build
    # Homebrew's ncurses and pulseaudio are keg-only, so their headers and
    # pkg-config files aren't on the default search paths. Point CMake/pkg-config
    # at them explicitly, pass the Homebrew prefix so the other deps (itpp,
    # codec2, libsndfile, portaudio) are found, and set the legacy CMake policy
    # floor in case dsd-fme's minimum is too old.
    NCURSES_PREFIX="$(brew --prefix ncurses)"
    export PKG_CONFIG_PATH="$(brew --prefix pulseaudio)/lib/pkgconfig:$NCURSES_PREFIX/lib/pkgconfig:$PKG_CONFIG_PATH"
    cmake -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
          -DCMAKE_PREFIX_PATH="$(brew --prefix)" \
          -DCURSES_NEED_NCURSES=TRUE \
          -DCURSES_INCLUDE_PATH="$NCURSES_PREFIX/include" \
          -DCURSES_LIBRARY="$NCURSES_PREFIX/lib/libncurses.dylib" \
          ..
    make -j"$NPROC"
    sudo make install
    cd "$PROJECT_ROOT"
    log_success "Radio dependencies installed."
else
    log_success "Radio dependencies found: $(command -v dsd-fme)"
fi

if [ "$DEPS_ONLY" = true ]; then
    log_success "Dependencies installed. Skipping build (--deps-only)."
    exit 0
fi

# ----------------------------------------------------------------
# 6. Build Application (.NET build also compiles the React client)
# ----------------------------------------------------------------
log_step "Building Application..."
cd "$PROJECT_ROOT/server/OpenScanner.Server"
if dotnet build -c Release; then
    log_success "Build succeeded."
else
    log_error "Build failed."
    exit 1
fi

# ----------------------------------------------------------------
# 7. Done
# ----------------------------------------------------------------
echo ""
echo "================================================="
log_success "Setup Complete!"
echo "================================================="
echo -e "   ${BOLD}Run OpenScanner:${NC}"
echo "     cd $PROJECT_ROOT/server/OpenScanner.Server"
echo "     dotnet run -c Release --urls \"http://0.0.0.0:8080\""
echo ""
echo -e "   Then open: ${BOLD}http://localhost:8080${NC}  (Swagger: /swagger)"
echo ""
echo -e "   ${BOLD}No SDR hardware?${NC} Run with the mock radio source by setting"
echo -e "   \"Radio\": { \"Provider\": \"Mock\" } in appsettings.Development.json."
echo "================================================="
