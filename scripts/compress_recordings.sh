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
# It is safe to re-run: files already converted are skipped, and a WAV is only
# deleted after its MP3 exists and (when a matching DB row is present) the
# database has been updated.
#
# Recommended: stop the service first so the database isn't locked:
#   systemctl --user stop openscanner   # or: sudo systemctl stop openscanner
#
# Usage:
#   scripts/compress_recordings.sh [--dry-run] [--bitrate 32k] [--data-dir PATH]
# ============================================================================

# Colors
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; BLUE='\033[0;34m'; NC='\033[0m'; BOLD='\033[1m'
log_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
log_step() { echo -e "\n${BLUE}${BOLD}==> $1${NC}"; }
log_success() { echo -e "${GREEN}[OK]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

PROJECT_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)

# Defaults
DRY_RUN=false
BITRATE="32k"
DATA_DIR="$PROJECT_ROOT/data"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run) DRY_RUN=true; shift ;;
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

# --- Preconditions ---------------------------------------------------------
command -v ffmpeg >/dev/null 2>&1 || { log_error "ffmpeg not found on PATH. Install it (e.g. sudo apt install ffmpeg)."; exit 1; }

HAVE_SQLITE=true
if ! command -v sqlite3 >/dev/null 2>&1; then
  HAVE_SQLITE=false
  log_warn "sqlite3 not found — files will be converted but the database will NOT be updated."
  log_warn "Install it (sudo apt install sqlite3) and re-run to fix playback links, or the app will 404 on old rows."
fi

if [ ! -d "$RECORDINGS_DIR" ]; then
  log_error "Recordings directory not found: $RECORDINGS_DIR"
  log_error "Pass --data-dir if your data lives elsewhere."
  exit 1
fi

if [ "$HAVE_SQLITE" = true ] && [ ! -f "$DB_PATH" ]; then
  log_warn "Database not found at $DB_PATH — converting files without DB updates."
  HAVE_SQLITE=false
fi

$DRY_RUN && log_warn "DRY RUN — no files will be changed."
log_info "Recordings: $RECORDINGS_DIR"
log_info "Database:   $DB_PATH"
log_info "Target:     MP3 mono @ $BITRATE"

# Portable file size in bytes.
filesize() { stat -c%s "$1" 2>/dev/null || stat -f%z "$1" 2>/dev/null || echo 0; }

human() {
  local b=$1 u=(B KB MB GB TB) i=0
  while [ "$b" -ge 1024 ] && [ "$i" -lt 4 ]; do b=$((b / 1024)); i=$((i + 1)); done
  echo "${b} ${u[$i]}"
}

# Update transmissions.audio_path from the WAV basename to the MP3 basename.
# Returns non-zero on failure so the caller can keep the WAV for a later retry.
db_update() {
  local wav_base="$1" mp3_base="$2"
  # Escape single quotes for SQL string literals.
  local w="${wav_base//\'/\'\'}" m="${mp3_base//\'/\'\'}"
  # Redirect stdout so PRAGMA's returned value isn't printed; keep stderr for errors.
  sqlite3 "$DB_PATH" >/dev/null <<SQL
PRAGMA busy_timeout=15000;
UPDATE transmissions SET audio_path='$m' WHERE audio_path='$w';
SQL
}

# --- Main loop -------------------------------------------------------------
log_step "Scanning for WAV recordings..."

shopt -s nullglob
wavs=("$RECORDINGS_DIR"/*.wav "$RECORDINGS_DIR"/*.WAV)
shopt -u nullglob

if [ "${#wavs[@]}" -eq 0 ]; then
  log_success "No WAV recordings found — nothing to do."
  exit 0
fi

log_info "Found ${#wavs[@]} WAV file(s)."

converted=0; skipped=0; failed=0
bytes_before=0; bytes_after=0

for wav in "${wavs[@]}"; do
  base=$(basename "$wav")
  mp3="${wav%.*}.mp3"
  mp3_base=$(basename "$mp3")
  wav_size=$(filesize "$wav")

  if $DRY_RUN; then
    log_info "Would convert: $base ($(human "$wav_size")) -> $mp3_base"
    bytes_before=$((bytes_before + wav_size))
    converted=$((converted + 1))
    continue
  fi

  # Reuse an existing MP3 if a previous run already produced one; otherwise encode.
  if [ -f "$mp3" ] && [ "$(filesize "$mp3")" -gt 0 ]; then
    log_info "MP3 already exists for $base — reconciling DB/cleanup only."
  else
    if ! ffmpeg -nostdin -y -loglevel error -i "$wav" -codec:a libmp3lame -b:a "$BITRATE" -ac 1 "$mp3" </dev/null; then
      log_error "ffmpeg failed on $base — leaving original in place."
      rm -f "$mp3" 2>/dev/null || true
      failed=$((failed + 1))
      continue
    fi
  fi

  if [ ! -f "$mp3" ] || [ "$(filesize "$mp3")" -eq 0 ]; then
    log_error "Conversion produced no MP3 for $base — leaving original in place."
    rm -f "$mp3" 2>/dev/null || true
    failed=$((failed + 1))
    continue
  fi

  # Point the DB row at the new file before deleting the old one.
  if [ "$HAVE_SQLITE" = true ]; then
    if ! db_update "$base" "$mp3_base"; then
      log_warn "DB update failed for $base (locked?) — keeping WAV so you can re-run. MP3 was created."
      failed=$((failed + 1))
      continue
    fi
  fi

  mp3_size=$(filesize "$mp3")
  rm -f "$wav"
  bytes_before=$((bytes_before + wav_size))
  bytes_after=$((bytes_after + mp3_size))
  converted=$((converted + 1))
  log_success "$base ($(human "$wav_size")) -> $mp3_base ($(human "$mp3_size"))"
done

# --- Summary ---------------------------------------------------------------
log_step "Summary"
if $DRY_RUN; then
  log_info "Would convert $converted file(s), reclaiming up to ~$(human "$bytes_before") (minus new MP3s)."
  log_info "Re-run without --dry-run to apply."
else
  freed=$((bytes_before - bytes_after))
  [ "$freed" -lt 0 ] && freed=0
  log_success "Converted: $converted   Failed/kept: $failed"
  log_success "Disk reclaimed: $(human "$freed")  ($(human "$bytes_before") WAV -> $(human "$bytes_after") MP3)"
  [ "$failed" -gt 0 ] && log_warn "Some files were kept; fix the cause (e.g. stop the service, install sqlite3) and re-run."
fi
