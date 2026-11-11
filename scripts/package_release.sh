#!/bin/bash
# package_release.sh — build a release tarball for one runtime.
#
#   scripts/package_release.sh <linux-x64|linux-arm64> [out_dir] [--whisper-dir <dir>]
#
# Publishes the server self-contained for the given runtime (so devices need no
# .NET runtime or SDK), builds the React client, and assembles
# <out_dir>/openscanner-<rid>.tar.gz — the artifact that install.sh and the in-app
# updater consume. See README "Releases" for the layout.
#
# --whisper-dir points at the output of build_whisper.sh for the *same*
# architecture. Without it the tarball ships no whisper-cli and the installer
# builds one on the device.
set -euo pipefail

RID=""
OUT=""
WHISPER_DIR=""
while [ $# -gt 0 ]; do
    case "$1" in
        --whisper-dir) WHISPER_DIR="${2:?--whisper-dir needs a path}"; shift 2 ;;
        -*) echo "Unknown option: $1" >&2; exit 2 ;;
        *) if [ -z "$RID" ]; then RID="$1"; else OUT="$1"; fi; shift ;;
    esac
done
case "$RID" in
    linux-x64|linux-arm64) ;;
    *) echo "Usage: $0 <linux-x64|linux-arm64> [out_dir] [--whisper-dir <dir>]" >&2; exit 2 ;;
esac

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
OUT="${OUT:-$ROOT/dist}"
mkdir -p "$OUT"
OUT=$(cd "$OUT" && pwd)

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
APP="$STAGE/openscanner"
mkdir -p "$APP"

echo "==> Building client"
(cd "$ROOT/client" && npm ci --no-audit --no-fund && npm run build)
mkdir -p "$APP/client"
cp -a "$ROOT/client/dist" "$APP/client/dist"

echo "==> Publishing server ($RID, self-contained)"
dotnet publish "$ROOT/server/OpenScanner.Server" -c Release -r "$RID" --self-contained true \
    -p:SkipFrontendBuild=true -o "$APP/server"

# The version nbgv stamps into the build, e.g. 0.1.300+abc1234def.
VERSION=$(dotnet msbuild "$ROOT/server/OpenScanner.Server" -t:GetBuildVersion \
    -getProperty:AssemblyInformationalVersion -p:SkipFrontendBuild=true 2>/dev/null | tail -n1 || true)
echo "${VERSION:-unknown}" > "$APP/VERSION"

if [ -n "$WHISPER_DIR" ]; then
    [ -x "$WHISPER_DIR/bin/whisper-cli" ] || { echo "No whisper-cli in $WHISPER_DIR" >&2; exit 1; }
    cp -a "$WHISPER_DIR" "$APP/whisper"
else
    echo "==> No --whisper-dir given: tarball will not include whisper-cli"
fi

mkdir -p "$APP/scripts"
for s in install.sh apply_update.sh uninstall.sh build_whisper.sh; do
    install -m 755 "$ROOT/scripts/$s" "$APP/scripts/$s"
done
cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY-NOTICES.md" "$APP/"

TARBALL="$OUT/openscanner-$RID.tar.gz"
tar -C "$STAGE" --owner=0 --group=0 -czf "$TARBALL" openscanner
echo "==> $TARBALL ($(du -h "$TARBALL" | cut -f1), version ${VERSION:-unknown})"
