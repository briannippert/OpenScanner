#!/bin/bash
set -euo pipefail

# ============================================================================
# OpenScanner — Compress old WAV recordings to MP3
# ============================================================================
# Recordings used to be stored as 48kHz mono WAV (~96 KB/s). They are now saved
# as 32 kbps mono MP3 (~4 KB/s) to save disk. This one-off maintenance script
# transcodes any leftover *.wav recordings to the same MP3 format, updates the
# database so playback keeps working, and deletes the original WAV.
#
# It converts in parallel (default: one job per CPU core) and applies all
# database changes in a single transaction, so it scales to tens of thousands
# of files. While updating the DB it also corrects each row's stored duration
# (derived from the audio bytes: 48kHz mono 16-bit = 96000 bytes/sec), which
# fixes older recordings whose duration was recorded as wall-clock time.
#
# It is safe to re-run: files already converted are skipped, and a WAV is only
# deleted after its MP3 exists and the database transaction has committed.
#
# Recommended: stop the service first so the database isn't locked:
#   systemctl --user stop openscanner   # or: sudo systemctl stop openscanner
#
# Usage:
#   scripts/compress_recordings.sh [--dry-run] [--jobs N] [--bitrate 32k] [--data-dir PATH]
# ============================================================================

# Colors
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; BLUE='\033[0;34m'; NC='\033[0m'; BOLD='\033[1m'
log_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
log_step() { echo -e "\n${BLUE}${BOLD}==> $1${NC}"; }
log_success() { echo -e "${GREEN}[OK]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

PROJECT_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)

cpu_count() { nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 4; }

# Defaults
DRY_RUN=false
BITRATE="32k"
DATA_DIR="$PROJECT_ROOT/data"
JOBS="$(cpu_count)"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run) DRY_RUN=true; shift ;;
    --jobs) JOBS="$2"; shift 2 ;;
    --bitrate) BITRATE="$2"; shift 2 ;;
    --data-dir) DATA_DIR="$2"; shift 2 ;;
    -h|--help)
      grep '^#' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *) log_error "Unknown argument: $1"; exit 1 ;;
  esac
done

RECORDINGS_DIR="$DATA_DIR/recordings"
DB_PATH="$DATA_DIR/openscanner.db"

# Raw audio is 48 kHz mono 16-bit PCM.
RAW_BYTES_PER_SEC=96000

# --- Preconditions ---------------------------------------------------------
command -v ffmpeg  >/dev/null 2>&1 || { log_error "ffmpeg not found on PATH. Install it (e.g. sudo apt install ffmpeg)."; exit 1; }
command -v sqlite3 >/dev/null 2>&1 || { log_error "sqlite3 not found on PATH. Install it (e.g. sudo apt install sqlite3)."; exit 1; }

[ -d "$RECORDINGS_DIR" ] || { log_error "Recordings directory not found: $RECORDINGS_DIR (pass --data-dir)"; exit 1; }
[ -f "$DB_PATH" ]        || { log_error "Database not found: $DB_PATH (pass --data-dir)"; exit 1; }
[[ "$JOBS" =~ ^[0-9]+$ ]] && [ "$JOBS" -ge 1 ] || { log_error "--jobs must be a positive integer"; exit 1; }

$DRY_RUN && log_warn "DRY RUN — no files will be changed."
log_info "Recordings: $RECORDINGS_DIR"
log_info "Database:   $DB_PATH"
log_info "Target:     MP3 mono @ $BITRATE   Jobs: $JOBS"

filesize() { stat -c%s "$1" 2>/dev/null || stat -f%z "$1" 2>/dev/null || echo 0; }
human() {
  local b=$1 u=(B KB MB GB TB) i=0
  while [ "$b" -ge 1024 ] && [ "$i" -lt 4 ]; do b=$((b / 1024)); i=$((i + 1)); done
  echo "${b} ${u[$i]}"
}

# --- Gather WAVs -----------------------------------------------------------
log_step "Scanning for WAV recordings..."
shopt -s nullglob
wavs=("$RECORDINGS_DIR"/*.wav "$RECORDINGS_DIR"/*.WAV)
shopt -u nullglob

total=${#wavs[@]}
if [ "$total" -eq 0 ]; then
  log_success "No WAV recordings found — nothing to do."
  exit 0
fi
log_info "Found $total WAV file(s)."

if $DRY_RUN; then
  bytes=0
  for w in "${wavs[@]}"; do bytes=$((bytes + $(filesize "$w"))); done
  log_step "Summary (dry run)"
  log_info "Would convert $total file(s), reclaiming up to ~$(human "$bytes") (minus new MP3s)."
  log_info "Re-run without --dry-run to apply."
  exit 0
fi

# --- Phase 1: convert in parallel -----------------------------------------
# Each worker converts one WAV -> MP3 and, on success, appends a tab-separated
# record (wav_path, wav_size, mp3_size) to the manifest. No DB writes or deletes
# happen here, so there is zero database contention across jobs.
MANIFEST=$(mktemp)
FAILLOG=$(mktemp)
trap 'rm -f "$MANIFEST" "$FAILLOG"' EXIT

export BITRATE MANIFEST FAILLOG

convert_one() {
  local wav="$1"
  local mp3="${wav%.*}.mp3"
  size() { stat -c%s "$1" 2>/dev/null || stat -f%z "$1" 2>/dev/null || echo 0; }

  if [ ! -f "$mp3" ] || [ "$(size "$mp3")" -eq 0 ]; then
    if ! ffmpeg -nostdin -y -loglevel error -i "$wav" -codec:a libmp3lame -b:a "$BITRATE" -ac 1 "$mp3" </dev/null; then
      rm -f "$mp3" 2>/dev/null || true
      printf '%s\n' "$wav" >> "$FAILLOG"
      return 0
    fi
  fi
  if [ ! -f "$mp3" ] || [ "$(size "$mp3")" -eq 0 ]; then
    rm -f "$mp3" 2>/dev/null || true
    printf '%s\n' "$wav" >> "$FAILLOG"
    return 0
  fi
  # Short line; append is atomic (< PIPE_BUF).
  printf '%s\t%s\t%s\n' "$wav" "$(size "$wav")" "$(size "$mp3")" >> "$MANIFEST"
}
export -f convert_one

log_step "Converting $total file(s) with $JOBS parallel job(s)..."
# Progress watcher.
( while :; do
    done=$(wc -l < "$MANIFEST" 2>/dev/null | tr -d ' ' || echo 0)
    fail=$(wc -l < "$FAILLOG" 2>/dev/null | tr -d ' ' || echo 0)
    printf "\r${BLUE}[INFO]${NC} Converted %s/%s (failed %s)   " "$done" "$total" "$fail"
    sleep 3
  done ) &
WATCHER=$!

printf '%s\0' "${wavs[@]}" | xargs -0 -P "$JOBS" -I{} bash -c 'convert_one "$@"' _ {}

kill "$WATCHER" 2>/dev/null || true
wait "$WATCHER" 2>/dev/null || true
printf "\n"

converted=$(wc -l < "$MANIFEST" | tr -d ' ')
failed=$(wc -l < "$FAILLOG" | tr -d ' ')
log_info "Converted $converted file(s); $failed failed."

if [ "$converted" -eq 0 ]; then
  log_warn "Nothing converted. Failed files left untouched."
  exit 1
fi

# --- Phase 2: one DB transaction, then delete originals --------------------
log_step "Updating database (single transaction)..."
# Build SQL: for each converted file, point audio_path at the MP3 and correct
# the duration from the audio byte count.
build_sql() {
  echo "PRAGMA busy_timeout=30000;"
  echo "BEGIN;"
  while IFS=$'\t' read -r wav wsz _msz; do
    local base mp3base dur w m
    base=$(basename "$wav")
    mp3base="${base%.*}.mp3"
    dur=$(awk "BEGIN{printf \"%.3f\", $wsz/$RAW_BYTES_PER_SEC}")
    w="${base//\'/\'\'}"; m="${mp3base//\'/\'\'}"
    echo "UPDATE transmissions SET audio_path='$m', duration=$dur WHERE audio_path='$w';"
  done < "$MANIFEST"
  echo "COMMIT;"
}

if build_sql | sqlite3 "$DB_PATH" >/dev/null; then
  log_success "Database updated for $converted file(s)."
else
  log_error "Database transaction failed — WAV files kept so you can re-run. MP3s were created."
  exit 1
fi

log_step "Deleting original WAV files..."
bytes_before=0; bytes_after=0
while IFS=$'\t' read -r wav wsz msz; do
  rm -f "$wav" && bytes_before=$((bytes_before + wsz)) && bytes_after=$((bytes_after + msz))
done < "$MANIFEST"

# --- Summary ---------------------------------------------------------------
freed=$((bytes_before - bytes_after)); [ "$freed" -lt 0 ] && freed=0
log_step "Summary"
log_success "Converted: $converted   Failed/kept: $failed"
log_success "Disk reclaimed: $(human "$freed")   ($(human "$bytes_before") WAV -> $(human "$bytes_after") MP3)"
[ "$failed" -gt 0 ] && log_warn "Some files failed to convert and were left in place; re-run to retry them."
exit 0
