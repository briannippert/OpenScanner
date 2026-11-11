#!/bin/bash
# build_whisper.sh — build a self-contained whisper-cli from a pinned whisper.cpp.
#
#   scripts/build_whisper.sh <out_dir> [--native]
#
# Produces:
#   <out_dir>/bin/whisper-cli            statically linked against whisper/ggml
#   <out_dir>/download-ggml-model.sh     whisper.cpp's model downloader
#   <out_dir>/LICENSE                    whisper.cpp's MIT licence
#   <out_dir>/VERSION                    the whisper.cpp ref that was built
#   <out_dir>/REQUIRES                   /proc/cpuinfo flags the binary needs (portable builds)
#
# Default is a *portable* build for release artifacts: it targets a fixed CPU
# baseline rather than the build machine, and records that baseline in REQUIRES so
# the installer can tell whether a given device can run it. --native optimises for
# the current CPU instead; the installer uses that as its on-device fallback.
set -euo pipefail

WHISPER_CPP_REF="${WHISPER_CPP_REF:-v1.9.4}"
WHISPER_CPP_REPO="${WHISPER_CPP_REPO:-https://github.com/ggerganov/whisper.cpp.git}"

OUT=""
NATIVE=false
for arg in "$@"; do
    case "$arg" in
        --native) NATIVE=true ;;
        -*) echo "Unknown option: $arg" >&2; exit 2 ;;
        *) OUT="$arg" ;;
    esac
done
[ -n "$OUT" ] || { echo "Usage: $0 <out_dir> [--native]" >&2; exit 2; }
mkdir -p "$OUT"
OUT=$(cd "$OUT" && pwd)

SRC=$(mktemp -d)
trap 'rm -rf "$SRC"' EXIT

echo "==> Fetching whisper.cpp $WHISPER_CPP_REF"
git clone --quiet --depth 1 --branch "$WHISPER_CPP_REF" "$WHISPER_CPP_REPO" "$SRC/whisper.cpp"

CMAKE_ARGS=(
    -DCMAKE_BUILD_TYPE=Release
    -DBUILD_SHARED_LIBS=OFF
    -DWHISPER_BUILD_TESTS=OFF
    -DWHISPER_BUILD_SERVER=OFF
    "-DCMAKE_EXE_LINKER_FLAGS=-static-libstdc++ -static-libgcc"
)
REQUIRES=""
if [ "$NATIVE" = true ]; then
    CMAKE_ARGS+=(-DGGML_NATIVE=ON)
else
    CMAKE_ARGS+=(-DGGML_NATIVE=OFF)
    case "$(uname -m)" in
        aarch64|arm64)
            # Raspberry Pi 5 class (Cortex-A76) and newer. A Pi 4 (Cortex-A72) lacks
            # these and gets the installer's on-device build instead.
            CMAKE_ARGS+=(-DGGML_CPU_ARM_ARCH=armv8.2-a+dotprod+fp16)
            REQUIRES="asimddp asimdhp"
            ;;
        x86_64)
            # ggml's non-native x86 default: AVX2-era CPUs (Haswell, 2013 and newer).
            REQUIRES="avx avx2 fma f16c"
            ;;
    esac
fi

echo "==> Building whisper-cli (native=$NATIVE)"
cmake -S "$SRC/whisper.cpp" -B "$SRC/build" "${CMAKE_ARGS[@]}" > /dev/null
cmake --build "$SRC/build" --target whisper-cli -j"$(nproc)"

mkdir -p "$OUT/bin"
install -m 755 "$SRC/build/bin/whisper-cli" "$OUT/bin/whisper-cli"
install -m 755 "$SRC/whisper.cpp/models/download-ggml-model.sh" "$OUT/download-ggml-model.sh"
install -m 644 "$SRC/whisper.cpp/LICENSE" "$OUT/LICENSE"
echo "$WHISPER_CPP_REF" > "$OUT/VERSION"
echo "$REQUIRES" > "$OUT/REQUIRES"

echo "==> whisper-cli written to $OUT/bin/whisper-cli"
