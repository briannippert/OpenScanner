#!/bin/bash
# monitor_radiobox.sh
# POC: Monitor and decode 72.78 MHz radio box transmissions.
# Uses rtl_fm (multi-frequency squelch scan) piped through multimon-ng with all
# decoders enabled, since the signal type is unknown. Once the protocol is identified,
# a targeted decoder can be specified via MULTIMON_DECODERS below.
#
# Heartbeat monitoring: radio boxes send periodic check-in transmissions to confirm
# they are online. This script tracks last-seen time per frequency and raises a
# [MISSED HEARTBEAT] alert if a box goes silent longer than HEARTBEAT_INTERVAL_SECS.

set -euo pipefail

# ==========================================
# Configuration (adjust for your hardware)
# ==========================================
FREQ="72.78M"     # WNPG967 - Salem NH Fire Dept (500 boxes, licensed 2013)
SAMPLE_RATE="22050"        # Hz; multimon-ng expects 22050 or 44100
SQUELCH_LEVEL="5"          # rtl_fm requires non-zero squelch for multi-frequency scan mode.
                           # 5 = low threshold, catches weak signals. Raise if noise triggers false decodes.
GAIN="0"                   # 0 = automatic gain; set to dB value (e.g., 40) if needed
DEVICE_INDEX="0"           # RTL-SDR device index (0 for first dongle)
LOG_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/data"
LOG_FILE="$LOG_DIR/radiobox_$(date +%Y%m%d).log"
RECORD_AUDIO=false
RECORDINGS_DIR="$LOG_DIR/radiobox_recordings"
DIAGNOSE=false
DIAGNOSE_SECS=900   # seconds to record per frequency in diagnose mode (15 min = > 1 heartbeat cycle)
SINGLE_FREQ=""      # if set, monitor only this frequency at squelch=0 (no scan mode)
WATERFALL=false
WATERFALL_SPAN="200k"     # total bandwidth to display (centered on FREQ)
WATERFALL_BIN_SIZE="1k"   # frequency resolution per column
WATERFALL_INTERVAL="1"    # rtl_power integration period in seconds per row

# Decoders to pass to multimon-ng.
# "ALL" uses every available decoder (best for identifying an unknown signal).
# Once the protocol is known, replace with targeted flags, e.g.:
#   MULTIMON_DECODERS="-a POCSAG512 -a POCSAG1200 -a DTMF"
MULTIMON_DECODERS="ALL"

# ------------------------------------------
# Heartbeat Configuration
# ------------------------------------------
# How long (seconds) before a box is considered offline if no signal received.
# NFPA 72 requires supervisory signals (heartbeats) no more than 10 minutes apart.
HEARTBEAT_INTERVAL_SECS=600

# How often (seconds) the watchdog checks for missed heartbeats.
HEARTBEAT_CHECK_SECS=60

# Optional regex pattern to classify incoming decodes as heartbeats vs alarm events.
# Heartbeats are usually short repetitive check-in codes. Leave empty to tag all
# received signals as [DECODE] until the signal format is identified.
# Example once protocol is known: HEARTBEAT_PATTERN="^POCSAG.*Address:.*0000"
HEARTBEAT_PATTERN=""

# ==========================================
# Colors / Logging
# ==========================================
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m'
BOLD='\033[1m'

log_info()      { echo -e "${BLUE}[INFO]${NC} $1"; }
log_step()      { echo -e "\n${BLUE}${BOLD}==> $1${NC}"; }
log_success()   { echo -e "${GREEN}[OK]${NC} $1"; }
log_warn()      { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error()     { echo -e "${RED}[ERROR]${NC} $1"; }
log_decode()    { echo -e "${CYAN}[DECODE]${NC} $1"; }
log_heartbeat() { echo -e "${GREEN}[HEARTBEAT]${NC} $1"; }
log_missed_hb() { echo -e "${RED}[MISSED HEARTBEAT]${NC} $1"; }

# ==========================================
# Usage
# ==========================================
usage() {
    echo ""
    echo -e "${BOLD}Usage:${NC} $(basename "$0") [OPTIONS]"
    echo ""
    echo "Monitor and decode radio box transmissions on 72.78 MHz."
    echo ""
    echo -e "${BOLD}Options:${NC}"
    echo "  --record          Also save raw audio WAV files for manual analysis"
    echo "  --diagnose        Capture raw IQ (.cu8) from each frequency for protocol analysis"
    echo "                    (15 min per freq by default to catch at least one heartbeat)"
    echo "                    Use with Universal Radio Hacker to identify modulation type"
    echo "  --waterfall       Display a live ANSI spectrum waterfall (uses rtl_power)"
    echo "                    Shows ${WATERFALL_SPAN} bandwidth centered on ${FREQ} at ${WATERFALL_BIN_SIZE} resolution"
    echo "                    Note: cannot run concurrently with monitoring (dongle is shared)"
    echo "  --single FREQ     Monitor one frequency only at squelch=0 (no scan hopping)"
    echo "                    Best for confirming pipeline works and catching short bursts"
    echo "                    Example: --single 72.78M"
    echo "  --squelch N       Override squelch level (default: $SQUELCH_LEVEL; 0=open)"
    echo "  --gain N          Override RTL-SDR gain in dB (default: 0=auto)"
    echo "  --device N        RTL-SDR device index (default: $DEVICE_INDEX)"
    echo "  --log FILE        Override log file path (default: $LOG_FILE)"
    echo "  --heartbeat N     Override missed-heartbeat threshold in seconds (default: $HEARTBEAT_INTERVAL_SECS)"
    echo "  --help            Show this message"
    echo ""
    echo -e "${BOLD}Examples:${NC}"
    echo "  $(basename "$0")                     # Standard monitoring with auto gain"
    echo "  $(basename "$0") --squelch 0         # Open squelch (monitor constantly)"
    echo "  $(basename "$0") --gain 40 --record  # Manual gain + save WAV recordings"
    echo "  $(basename "$0") --diagnose           # Capture 15min raw IQ per freq (URH analysis)"
    echo "  $(basename "$0") --single 72.78M      # Single-freq mode, squelch=0, no scan hopping"
    echo "  $(basename "$0") --waterfall          # Live spectrum waterfall on 72.78 MHz"
    echo ""
    echo -e "${BOLD}Notes:${NC}"
    echo "  - This is a POC that runs ALL multimon-ng decoders to identify the signal type."
    echo "  - Once the protocol is known, set MULTIMON_DECODERS in this script for accuracy."
    echo "  - Set HEARTBEAT_PATTERN to a regex matching your system's check-in code."
    echo "  - Decoded output is logged to: $LOG_FILE"
    echo "  - If multimon-ng decodes nothing, run --diagnose to capture raw IQ files (.cu8),"
    echo "    then load them into Universal Radio Hacker (pip install urh) to identify"
    echo "    the modulation and protocol (https://github.com/jopohl/urh)."
    echo "  - The --diagnose mode uses rtl_sdr (raw IQ), NOT rtl_fm (FM audio), because"
    echo "    FM demodulation destroys the phase/frequency info needed to identify protocols."
    echo "  - If your RTL-SDR dongle cannot tune to 72 MHz, try setting GAIN to a fixed"
    echo "    value (e.g., --gain 40) or enable direct sampling in rtl_fm (-D 2 flag)."
    echo ""
    exit 0
}

# ==========================================
# Argument Parsing
# ==========================================
while [[ $# -gt 0 ]]; do
    case "$1" in
        --single)
            SINGLE_FREQ="$2"
            shift 2
            ;;
        --record)
            RECORD_AUDIO=true
            shift
            ;;
        --diagnose)
            DIAGNOSE=true
            shift
            ;;
        --waterfall)
            WATERFALL=true
            shift
            ;;
        --squelch)
            SQUELCH_LEVEL="$2"
            shift 2
            ;;
        --gain)
            GAIN="$2"
            shift 2
            ;;
        --device)
            DEVICE_INDEX="$2"
            shift 2
            ;;
        --log)
            LOG_FILE="$2"
            shift 2
            ;;
        --heartbeat)
            HEARTBEAT_INTERVAL_SECS="$2"
            shift 2
            ;;
        --help|-h)
            usage
            ;;
        *)
            log_error "Unknown option: $1"
            usage
            ;;
    esac
done

# ==========================================
# Dependency Check
# ==========================================
check_deps() {
    log_step "Checking Dependencies"
    local missing=false

    if ! command -v rtl_fm &>/dev/null; then
        log_error "rtl_fm not found. Install with: sudo apt-get install rtl-sdr"
        missing=true
    else
        log_success "rtl_fm: $(which rtl_fm)"
    fi

    if ! command -v multimon-ng &>/dev/null; then
        log_error "multimon-ng not found. Install with: sudo apt-get install multimon-ng"
        missing=true
    else
        log_success "multimon-ng: $(which multimon-ng)"
    fi

    if ! command -v sox &>/dev/null; then
        log_warn "sox not found (optional, used for WAV recording). Install with: sudo apt-get install sox"
        if [ "$RECORD_AUDIO" = true ]; then
            log_error "sox is required for --record mode."
            missing=true
        fi
    else
        log_success "sox: $(which sox)"
    fi

    if [ "$WATERFALL" = true ]; then
        if ! command -v rtl_power &>/dev/null; then
            log_error "rtl_power not found. Install with: sudo apt-get install rtl-sdr"
            missing=true
        else
            log_success "rtl_power: $(which rtl_power)"
        fi
        if ! command -v python3 &>/dev/null; then
            log_error "python3 not found. Install with: sudo apt-get install python3"
            missing=true
        else
            log_success "python3: $(which python3)"
        fi
    fi

    if [ "$missing" = true ]; then
        log_error "One or more required dependencies are missing. Exiting."
        exit 1
    fi
}

# ==========================================
# RTL-SDR Device Check
# ==========================================
check_device() {
    log_step "Checking RTL-SDR Device"
    # rtl_test -t does a quick open/close without blocking; exit code 0 = device found
    if rtl_test -t -d "$DEVICE_INDEX" &>/dev/null; then
        log_success "RTL-SDR device $DEVICE_INDEX found."
    else
        log_error "No RTL-SDR device found at index $DEVICE_INDEX."
        log_error "Check that the dongle is plugged in and not in use by another process."
        log_error "If another process has it open: sudo lsof /dev/bus/usb/*/*"
        exit 1
    fi
}

# ==========================================
# Process Cleanup
# ==========================================
RTL_FM_PID=""
MULTIMON_PID=""
SOX_PID=""
WATCHDOG_PID=""
FREQ_TRACKER_PID=""
NAMED_PIPE=""
HB_STATE_DIR="/tmp/radiobox_$$"

cleanup() {
    echo ""
    log_warn "Shutting down monitor..."
    [ -n "$RTL_FM_PID" ]       && kill "$RTL_FM_PID"       2>/dev/null || true
    [ -n "$MULTIMON_PID" ]     && kill "$MULTIMON_PID"     2>/dev/null || true
    [ -n "$SOX_PID" ]          && kill "$SOX_PID"          2>/dev/null || true
    [ -n "$WATCHDOG_PID" ]     && kill "$WATCHDOG_PID"     2>/dev/null || true
    [ -n "$FREQ_TRACKER_PID" ] && kill "$FREQ_TRACKER_PID" 2>/dev/null || true
    [ -n "$NAMED_PIPE" ]       && rm -f "$NAMED_PIPE"
    rm -rf "$HB_STATE_DIR"
    log_info "Stopped. Log saved to: $LOG_FILE"
    exit 0
}

trap cleanup SIGINT SIGTERM

# ==========================================
# Startup Banner
# ==========================================
print_banner() {
    echo ""
    echo -e "${BOLD}=================================================${NC}"
    echo -e "${BOLD}  OpenScanner - Radio Box Monitor (POC)${NC}"
    echo -e "${BOLD}=================================================${NC}"
    echo -e "  ${BOLD}Frequency:${NC}   $FREQ"
    echo -e "  ${BOLD}Sample Rate:${NC}  ${SAMPLE_RATE} Hz"
    echo -e "  ${BOLD}Squelch:${NC}      $SQUELCH_LEVEL"
    echo -e "  ${BOLD}Gain:${NC}         $([ "$GAIN" = "0" ] && echo "auto" || echo "${GAIN} dB")"
    echo -e "  ${BOLD}Device:${NC}       index $DEVICE_INDEX"
    echo -e "  ${BOLD}Decoders:${NC}     $([ "$MULTIMON_DECODERS" = "ALL" ] && echo "ALL (protocol identification mode)" || echo "$MULTIMON_DECODERS")"
    echo -e "  ${BOLD}Log File:${NC}     $LOG_FILE"
    echo -e "  ${BOLD}Record Audio:${NC} $([ "$RECORD_AUDIO" = true ] && echo "yes -> $RECORDINGS_DIR" || echo "no")"
    echo -e "  ${BOLD}Diagnose:${NC}     $([ "$DIAGNOSE" = true ] && echo "yes - capturing ${DIAGNOSE_SECS}s raw IQ per frequency" || echo "no")"
    echo -e "  ${BOLD}Single Freq:${NC}  $([ -n "$SINGLE_FREQ" ] && echo "$SINGLE_FREQ (squelch=0, no scan)" || echo "no (scanning all)")"
    echo -e "  ${BOLD}Waterfall:${NC}    $([ "$WATERFALL" = true ] && echo "yes - ${WATERFALL_SPAN} span, ${WATERFALL_BIN_SIZE} bins, ${WATERFALL_INTERVAL}s/row" || echo "no")"
    echo -e "  ${BOLD}Heartbeat:${NC}    alert after ${HEARTBEAT_INTERVAL_SECS}s silence per frequency (check every ${HEARTBEAT_CHECK_SECS}s)"
    echo -e "${BOLD}=================================================${NC}"
    echo -e "  Press ${BOLD}Ctrl+C${NC} to stop."
    echo -e "${BOLD}=================================================${NC}"
    echo ""
}

# ==========================================
# Build multimon-ng decoder arguments
# ==========================================
build_multimon_args() {
    if [ "$MULTIMON_DECODERS" = "ALL" ]; then
        echo "-A"
    else
        echo "$MULTIMON_DECODERS"
    fi
}

# ==========================================
# Frequency conversion: "72.78M" -> "72780000"
# ==========================================
freq_to_hz() {
    local freq="$1"
    if [[ "$freq" =~ ^([0-9]+\.?[0-9]*)M$ ]]; then
        awk "BEGIN {printf \"%d\", ${BASH_REMATCH[1]} * 1000000}"
    elif [[ "$freq" =~ ^([0-9]+\.?[0-9]*)k$ ]]; then
        awk "BEGIN {printf \"%d\", ${BASH_REMATCH[1]} * 1000}"
    else
        echo "$freq"
    fi
}

# ==========================================
# Frequency tracker
# Reads rtl_fm stderr lines and updates the current-frequency state file so
# log_decoded_output can attribute each decode to the correct frequency.
# rtl_fm prints "Tuned to XXXXXX Hz." when it switches frequencies.
# ==========================================
freq_tracker() {
    local current_freq_file="$HB_STATE_DIR/current_freq"
    while IFS= read -r line; do
        if [[ "$line" =~ Tuned\ to\ ([0-9]+)\ Hz ]]; then
            echo "${BASH_REMATCH[1]}" > "$current_freq_file"
        fi
    done
}

# ==========================================
# Heartbeat watchdog (runs in background)
# Checks each frequency's last-seen file every HEARTBEAT_CHECK_SECS.
# Logs [MISSED HEARTBEAT] if elapsed > HEARTBEAT_INTERVAL_SECS.
# ==========================================
run_heartbeat_watchdog() {
    local freq_hz
    freq_hz=$(freq_to_hz "$FREQ")

    # Initialize state with the current time so we don't false-alarm at startup
    local epoch
    epoch=$(date +%s)
    echo "$epoch" > "$HB_STATE_DIR/hb_${freq_hz}"

    while true; do
        sleep "$HEARTBEAT_CHECK_SECS" || true
        local now
        now=$(date +%s)

        for entry in "${freq_hz}:${FREQ}"; do
            local hz="${entry%%:*}"
            local label="${entry##*:}"
            local state_file="$HB_STATE_DIR/hb_${hz}"

            [ -f "$state_file" ] || continue
            local last_seen
            last_seen=$(cat "$state_file")
            local elapsed=$(( now - last_seen ))

            if [ "$elapsed" -gt "$HEARTBEAT_INTERVAL_SECS" ]; then
                local ts
                ts="$(date '+%Y-%m-%d %H:%M:%S')"
                local msg="[$ts] [MISSED HEARTBEAT] No signal from $label for ${elapsed}s (threshold: ${HEARTBEAT_INTERVAL_SECS}s)"
                log_missed_hb "[$ts] No signal from $label for ${elapsed}s (threshold: ${HEARTBEAT_INTERVAL_SECS}s)"
                echo "$msg" >> "$LOG_FILE"
            fi
        done
    done
}

# ==========================================
# Timestamped decode logger
# Reads decoded lines from stdin, attributes them to the active frequency,
# updates heartbeat state, and writes to terminal + log file.
# ==========================================
log_decoded_output() {
    local freq_hz
    freq_hz=$(freq_to_hz "$FREQ")
    local current_freq_file="$HB_STATE_DIR/current_freq"

    while IFS= read -r line; do
        # Skip blank lines and multimon-ng initialization/status messages
        [[ -z "$line" ]] && continue
        [[ "$line" == "^"* ]] && continue
        [[ "$line" == "Enabled demodulators:"* ]] && continue
        [[ "$line" == "This is free software"* ]] && continue

        local ts epoch
        ts="$(date '+%Y-%m-%d %H:%M:%S')"
        epoch="$(date +%s)"

        # Determine which frequency is currently active
        local current_hz=""
        [ -f "$current_freq_file" ] && current_hz="$(cat "$current_freq_file")"

        local freq_label=""
        if [ -n "$current_hz" ]; then
            freq_label=" [$(awk "BEGIN {printf \"%.4f\", $current_hz / 1000000}") MHz]"

            # Update heartbeat state for the active frequency
            if [ "$current_hz" = "$freq_hz" ]; then
                echo "$epoch" > "$HB_STATE_DIR/hb_${freq_hz}"
            fi
        else
            # rtl_fm hasn't reported a tune yet; update as best-effort
            echo "$epoch" > "$HB_STATE_DIR/hb_${freq_hz}"
        fi

        # Classify: heartbeat check-in vs alarm/unknown event
        local formatted
        if [ -n "$HEARTBEAT_PATTERN" ] && echo "$line" | grep -qE "$HEARTBEAT_PATTERN"; then
            formatted="[$ts]${freq_label} [HEARTBEAT] $line"
            log_heartbeat "[$ts]${freq_label} $line"
        else
            formatted="[$ts]${freq_label} [DECODE] $line"
            log_decode "[$ts]${freq_label} $line"
        fi

        echo "$formatted" >> "$LOG_FILE"
    done
}

# ==========================================
# Single-frequency pipeline
# Monitors one frequency at squelch=0 — no scan hopping.
# This is the most reliable way to catch short transmissions and confirm
# that the pipeline produces output at all.
# ==========================================
run_pipeline_single() {
    local multimon_args="$1"
    local stderr_pipe="$2"
    local freq="$3"

    set +e
    rtl_fm \
        -M am \
        -f "$freq" \
        -s "$SAMPLE_RATE" \
        -l 0 \
        -g "$GAIN" \
        -d "$DEVICE_INDEX" \
        - 2>"$stderr_pipe" \
    | multimon-ng $multimon_args -t raw - 2>/dev/null \
    | log_decoded_output
    local exit_code=$?
    set -e
    return $exit_code
}

# ==========================================
# Core rtl_fm pipeline (single run attempt)
# Returns non-zero if rtl_fm exits unexpectedly.
# ==========================================
run_pipeline() {
    local multimon_args="$1"
    local stderr_pipe="$2"

    # rtl_fm requires non-zero squelch for multi-frequency scan mode.
    if [ "$SQUELCH_LEVEL" -eq 0 ] 2>/dev/null; then
        log_error "Squelch must be non-zero for multi-frequency scan (rtl_fm requirement)."
        log_error "Using squelch 5. Override with --squelch N (N >= 1)."
        SQUELCH_LEVEL=5
    fi

    # Disable set -e for the pipeline: rtl_fm will exit non-zero on device loss
    # and we want to handle that gracefully with a retry rather than killing the script.
    set +e
    rtl_fm \
        -M am \
        -f "$FREQ" \
        -s "$SAMPLE_RATE" \
        -l "$SQUELCH_LEVEL" \
        -g "$GAIN" \
        -d "$DEVICE_INDEX" \
        - 2>"$stderr_pipe" \
    | multimon-ng $multimon_args -t raw - 2>/dev/null \
    | log_decoded_output
    local exit_code=$?
    set -e
    return $exit_code
}

# ==========================================
# Single-frequency monitor
# ==========================================
run_monitor_single() {
    local multimon_args
    multimon_args="$(build_multimon_args)"
    local freq="$SINGLE_FREQ"

    log_step "Starting Radio Box Monitor (single frequency: $freq)"
    log_info "Monitoring $freq only, squelch=0 (open) — no scan hopping."
    log_info "This guarantees every transmission on $freq is heard."
    log_warn "Note: multimon-ng may not have a decoder for this protocol."
    log_warn "If you see no DECODE lines after 15+ min, run --diagnose for IQ capture."
    log_info "Decoded output will appear below (and be saved to the log file):"
    echo ""

    # Heartbeat watchdog still runs, but tracks only this frequency
    run_heartbeat_watchdog &
    WATCHDOG_PID=$!

    local stderr_pipe
    stderr_pipe=$(mktemp -u /tmp/radiobox_stderr_XXXXXX)
    mkfifo "$stderr_pipe"
    freq_tracker < "$stderr_pipe" &
    FREQ_TRACKER_PID=$!

    local retry_delay=5
    while true; do
        if ! run_pipeline_single "$multimon_args" "$stderr_pipe" "$freq"; then
            log_warn "rtl_fm exited unexpectedly. Retrying in ${retry_delay}s..."
            sleep "$retry_delay"
        fi
    done

    rm -f "$stderr_pipe"
}

# ==========================================
# Main Monitor (standard mode)
# ==========================================
run_monitor() {
    local multimon_args
    multimon_args="$(build_multimon_args)"

    log_step "Starting Radio Box Monitor"
    log_info "rtl_fm monitoring $FREQ with squelch $SQUELCH_LEVEL (AM demod)..."
    log_info "Heartbeat watchdog active: alert after ${HEARTBEAT_INTERVAL_SECS}s silence per frequency."
    log_info "Decoded output will appear below (and be saved to the log file):"
    echo ""

    # Start heartbeat watchdog in background
    run_heartbeat_watchdog &
    WATCHDOG_PID=$!

    # Named pipe to capture rtl_fm stderr for frequency tracking
    local stderr_pipe
    stderr_pipe=$(mktemp -u /tmp/radiobox_stderr_XXXXXX)
    mkfifo "$stderr_pipe"
    freq_tracker < "$stderr_pipe" &
    FREQ_TRACKER_PID=$!

    # Retry loop: if rtl_fm exits (device removed, USB glitch, etc.) wait and restart.
    local retry_delay=5
    while true; do
        # -M am       : AM demodulation (fire boxes use A1D/A2D - amplitude modulation with digital data)
        # -f          : frequency
        # -s          : sample rate
        # -l          : squelch level (0 = open)
        # -g          : tuner gain (0 = auto)
        # -d          : device index
        if ! run_pipeline "$multimon_args" "$stderr_pipe"; then
            log_warn "rtl_fm exited unexpectedly (device removed or error). Retrying in ${retry_delay}s..."
            log_warn "Check that the RTL-SDR dongle is connected: lsusb | grep -i rtl"
            sleep "$retry_delay"
        fi
    done

    rm -f "$stderr_pipe"
}

# ==========================================
# Main Monitor (record mode)
# Tees rtl_fm output: one copy to multimon-ng, one copy to sox for WAV recording.
# ==========================================
run_monitor_with_recording() {
    local multimon_args
    multimon_args="$(build_multimon_args)"

    mkdir -p "$RECORDINGS_DIR"

    log_step "Starting Radio Box Monitor (with audio recording)"
    log_info "rtl_fm monitoring $FREQ with squelch $SQUELCH_LEVEL (AM demod)..."
    log_info "Heartbeat watchdog active: alert after ${HEARTBEAT_INTERVAL_SECS}s silence per frequency."
    echo ""

    # Start heartbeat watchdog in background
    run_heartbeat_watchdog &
    WATCHDOG_PID=$!

    # Named pipe for rtl_fm stderr (frequency tracking)
    local stderr_pipe
    stderr_pipe=$(mktemp -u /tmp/radiobox_stderr_XXXXXX)
    mkfifo "$stderr_pipe"
    freq_tracker < "$stderr_pipe" &
    FREQ_TRACKER_PID=$!

    local retry_delay=5
    while true; do
        local wav_file="$RECORDINGS_DIR/radiobox_raw_$(date +%Y%m%d_%H%M%S).wav"
        log_info "Saving raw audio to: $wav_file"

        # Named pipe for sox audio recording (recreated on each retry)
        NAMED_PIPE=$(mktemp -u /tmp/radiobox_pipe_XXXXXX)
        mkfifo "$NAMED_PIPE"

        # sox reads from the named pipe and writes a WAV file
        sox -t raw -r "$SAMPLE_RATE" -e signed-integer -b 16 -c 1 \
            "$NAMED_PIPE" "$wav_file" &
        SOX_PID=$!

        set +e
        # rtl_fm writes to both the named pipe (for sox) and stdout (for multimon-ng)
        rtl_fm \
            -M am \
            -f "$FREQ" \
            -s "$SAMPLE_RATE" \
            -l "$SQUELCH_LEVEL" \
            -g "$GAIN" \
            -d "$DEVICE_INDEX" \
            - 2>"$stderr_pipe" \
        | tee "$NAMED_PIPE" \
        | multimon-ng $multimon_args -t raw - 2>/dev/null \
        | log_decoded_output
        local exit_code=$?
        set -e

        rm -f "$NAMED_PIPE"
        NAMED_PIPE=""
        [ -n "$SOX_PID" ] && kill "$SOX_PID" 2>/dev/null || true
        SOX_PID=""

        if [ $exit_code -ne 0 ]; then
            log_warn "rtl_fm exited unexpectedly (device removed or error). Retrying in ${retry_delay}s..."
            log_warn "Check that the RTL-SDR dongle is connected: lsusb | grep -i rtl"
            sleep "$retry_delay"
        fi
    done

    rm -f "$stderr_pipe"
}

# ==========================================
# Diagnose Mode - Capture raw IQ per frequency
# ==========================================
# Captures raw IQ data from each frequency using rtl_sdr for DIAGNOSE_SECS seconds.
# Raw IQ (complex uint8 from rtl_sdr) is what URH expects for proper protocol analysis.
# rtl_fm (FM-demodulated audio) destroys the phase/frequency info needed to identify
# modulation type. This mode bypasses rtl_fm entirely.
#
# Notes on frequency coverage:
#   - RTL-SDR dongles have poor sensitivity below ~100 MHz (72 MHz is marginal)
#   - To improve 72 MHz reception: use direct sampling mode (-D 2) if your dongle supports it
#   - URH can analyze the resulting .cu8 IQ files directly
run_diagnose() {
    log_step "Diagnose Mode - Capturing raw IQ per frequency (${DIAGNOSE_SECS}s each)"
    echo ""
    log_info "Using rtl_sdr to capture raw IQ data (complex uint8 .cu8 format)."
    log_info "This preserves the full signal structure so URH can identify modulation."
    log_info "FM-demodulated audio (rtl_fm) is NOT used here - it would destroy signal info."
    echo ""
    log_info "Each frequency is recorded for ${DIAGNOSE_SECS}s ($(( DIAGNOSE_SECS / 60 ))min)."
    log_info "NFPA 72 heartbeat interval is 600s - ${DIAGNOSE_SECS}s covers $(( DIAGNOSE_SECS / 600 )) full cycle(s)."
    echo ""
    log_info "Load .cu8 files into Universal Radio Hacker to identify the protocol:"
    log_info "  urh <file>.cu8"
    log_info "  GitHub: https://github.com/jopohl/urh"
    echo ""

    if ! command -v rtl_sdr &>/dev/null; then
        log_error "rtl_sdr not found. Install with: sudo apt-get install rtl-sdr"
        exit 1
    fi

    mkdir -p "$RECORDINGS_DIR"

    local gain_arg
    if [ "$GAIN" = "0" ] || [ "$GAIN" = "auto" ]; then
        gain_arg="0"
    else
        gain_arg="$GAIN"
    fi

    # Sample rate for IQ capture: 250k gives 250 kHz bandwidth around center
    local iq_rate="250000"

    for freq in "$FREQ"; do
        local safe_freq
        safe_freq=$(echo "$freq" | tr '.' '_')
        local ts
        ts=$(date '+%Y%m%d_%H%M%S')
        local iq_file="$RECORDINGS_DIR/iq_${safe_freq}_${ts}.cu8"

        local freq_hz
        # Convert rtl_fm notation (e.g. 72.78M) to Hz for rtl_sdr
        if [[ "$freq" == *M ]]; then
            freq_hz=$(echo "${freq%M} * 1000000" | bc | cut -d. -f1)
        elif [[ "$freq" == *k ]]; then
            freq_hz=$(echo "${freq%k} * 1000" | bc | cut -d. -f1)
        else
            freq_hz="$freq"
        fi

        local dur_min=$(( DIAGNOSE_SECS / 60 ))
        log_info "[$freq] Capturing IQ for ${DIAGNOSE_SECS}s (${dur_min}min) -> $(basename "$iq_file")"

        # Warn about 72 MHz sensitivity limitations
        if (( freq_hz < 100000000 )); then
            log_warn "[$freq] Below 100 MHz - RTL-SDR sensitivity is reduced here."
            log_warn "[$freq] If no signal found, try: direct sampling (-D 2 flag requires rtl_fm edit)"
            log_warn "[$freq] or use an upconverter for proper HF/VHF coverage."
        fi

        set +e
        timeout "$DIAGNOSE_SECS" rtl_sdr \
            -f "$freq_hz" \
            -s "$iq_rate" \
            -g "$gain_arg" \
            -d "$DEVICE_INDEX" \
            "$iq_file" 2>/dev/null
        local exit_code=$?
        set -e

        if [ -f "$iq_file" ] && [ -s "$iq_file" ]; then
            local size
            size=$(du -h "$iq_file" | cut -f1)
            local samples
            samples=$(wc -c < "$iq_file")
            local duration_actual=$(( samples / 2 / iq_rate ))
            log_success "[$freq] Saved: $(basename "$iq_file") ($size, ~${duration_actual}s)"
        else
            log_warn "[$freq] No data captured (dongle error or empty file)"
        fi
        echo ""
    done

    echo ""
    log_step "Diagnose Capture Complete"
    log_info "IQ files saved to: $RECORDINGS_DIR"
    echo ""
    log_info "Analysis steps:"
    log_info "  1. Open URH:  urh"
    log_info "  2. File > Open file, select a .cu8 file"
    log_info "  3. In 'Interpretation' tab, look for signal bursts in the spectrogram"
    log_info "  4. Right-click a burst -> 'Auto-detect signal parameters'"
    log_info "  5. Use 'Analysis' tab to decode bits and identify protocol"
    echo ""
    log_info "Expected signal characteristics:"
    log_info "  72.78 MHz  (Salem Fire, 2K00A1D): AM, single-channel digital, 500 boxes"
    echo ""
    log_info "If spectrogram shows no activity, the recording window may have missed"
    log_info "a heartbeat. NFPA 72 allows up to 600s between heartbeats. Try again"
    log_info "or increase DIAGNOSE_SECS to 900+ seconds for guaranteed coverage."
}

# ==========================================
# Waterfall Mode
# Runs rtl_power over a configurable bandwidth centered on FREQ and pipes
# the CSV output to an embedded Python 3 renderer that draws a scrolling
# ANSI 256-color spectrum waterfall in the terminal.
# Cannot run concurrently with monitoring — both need the RTL-SDR dongle.
# ==========================================
run_waterfall() {
    log_step "Starting Spectrum Waterfall"

    # Convert span/bin strings like "200k" or "1k" to Hz integers
    span_to_hz() {
        local val="$1"
        if [[ "$val" =~ ^([0-9]+)k$ ]]; then
            echo $(( ${BASH_REMATCH[1]} * 1000 ))
        elif [[ "$val" =~ ^([0-9]+)M$ ]]; then
            echo $(( ${BASH_REMATCH[1]} * 1000000 ))
        else
            echo "$val"
        fi
    }

    # Center frequency in Hz
    local center_hz
    center_hz=$(freq_to_hz "$FREQ")

    local span_hz
    span_hz=$(span_to_hz "$WATERFALL_SPAN")

    local bin_hz
    bin_hz=$(span_to_hz "$WATERFALL_BIN_SIZE")

    local low_hz=$(( center_hz - span_hz / 2 ))
    local high_hz=$(( center_hz + span_hz / 2 ))

    log_info "Frequency: ${FREQ} (center)"
    log_info "Range: ${low_hz} Hz - ${high_hz} Hz  (${WATERFALL_SPAN} span, ${WATERFALL_BIN_SIZE} bins)"
    log_info "Integration: ${WATERFALL_INTERVAL}s per row"
    log_info "Press Ctrl+C to stop."
    echo ""

    local gain_arg="$GAIN"

    # Write the Python renderer to a temp file so that python3 reads the script
    # from disk, leaving stdin free for the rtl_power pipe.
    local py_script
    py_script=$(mktemp /tmp/radiobox_wf_XXXXXX.py)
    # shellcheck disable=SC2064
    trap "rm -f '$py_script'; trap - EXIT" EXIT

    cat > "$py_script" <<'PYEOF'
import sys, os

# ANSI 256-color palette: dark blue -> cyan -> green -> yellow -> orange -> red -> white
PALETTE = [
    17, 18, 19, 20, 21,
    27, 33, 39, 45,
    51, 50, 49, 48, 47, 46,
    82, 118, 154, 190, 226,
    220, 214, 208, 202, 196,
    197, 198, 199, 200, 201,
    207, 213, 219, 225, 231,
]
N_COLORS = len(PALETTE)

def db_to_color(db, db_min, db_max):
    if db_max <= db_min:
        return PALETTE[0]
    ratio = max(0.0, min(1.0, (db - db_min) / (db_max - db_min)))
    return PALETTE[int(ratio * (N_COLORS - 1))]

RESET = "\033[0m"
BOLD  = "\033[1m"

def term_cols():
    try:
        return os.get_terminal_size().columns
    except Exception:
        return 80

row_count     = 0
HEADER_EVERY  = 20
WARMUP_ROWS   = 3
warmup_buf    = []
db_floor      = None
db_ceil       = None
center_hz     = None
low_hz_g      = None
high_hz_g     = None

def bin_cols(terminal_cols):
    """Number of frequency bins that fit, given each bin renders as 2 chars + 9 for timestamp."""
    return max(1, (terminal_cols - 9) // 2)

def print_header(n_bins, low_hz, high_hz, center_hz, cols):
    w = min(bin_cols(cols), n_bins)
    if w == 0:
        return
    left_mhz  = f"{low_hz/1e6:.3f}"
    cent_mhz  = f"{center_hz/1e6:.4f}"
    right_mhz = f"{high_hz/1e6:.3f}"
    if high_hz > low_hz:
        center_col = int((center_hz - low_hz) / (high_hz - low_hz) * (w - 1))
    else:
        center_col = w // 2
    ruler     = ['.'] * w
    label_row = [' '] * w
    if 0 <= center_col < w:
        ruler[center_col] = '^'
    for i, ch in enumerate(left_mhz):
        if i < w:
            label_row[i] = ch
    c_start = max(0, center_col - len(cent_mhz) // 2)
    for i, ch in enumerate(cent_mhz):
        pos = c_start + i
        if 0 <= pos < w:
            label_row[pos] = ch
    r_start = max(0, w - len(right_mhz))
    for i, ch in enumerate(right_mhz):
        pos = r_start + i
        if 0 <= pos < w:
            label_row[pos] = ch
    print("         " + BOLD + ''.join(label_row) + RESET)
    print("         " + BOLD + ''.join(ruler)     + RESET)

def render_row(bins, ts_str, cols):
    n = len(bins)
    w = min(bin_cols(cols), n)
    if w == 0:
        return
    cells = []
    for col in range(w):
        bin_idx = int(col / w * n)
        color = db_to_color(bins[bin_idx], db_floor, db_ceil)
        cells.append(f"\033[48;5;{color}m  ")
    ts_label = ts_str[-8:]  # HH:MM:SS
    print(f"{ts_label} {''.join(cells)}{RESET}", flush=True)

def parse_line(line):
    parts = [p.strip() for p in line.split(',')]
    if len(parts) < 7:
        return None
    try:
        dbs = [float(x) for x in parts[6:] if x.strip()]
        if not dbs:
            return None
        return parts[0], parts[1], float(parts[2]), float(parts[3]), dbs
    except (ValueError, IndexError):
        return None

for raw_line in sys.stdin:
    line = raw_line.strip()
    if not line:
        continue
    parsed = parse_line(line)
    if parsed is None:
        continue
    date_s, time_s, hz_low, hz_high, dbs = parsed

    if low_hz_g  is None: low_hz_g  = hz_low
    if high_hz_g is None or hz_high > high_hz_g: high_hz_g = hz_high
    if center_hz is None: center_hz = (low_hz_g + high_hz_g) / 2.0

    row_min, row_max = min(dbs), max(dbs)
    if db_floor is None:
        db_floor = row_min
        db_ceil  = row_max + 20
    else:
        db_floor = db_floor * 0.98 + row_min * 0.02
        db_ceil  = max(db_ceil, row_max)

    ts_str = f"{date_s} {time_s}"

    if row_count < WARMUP_ROWS:
        warmup_buf.append((dbs, ts_str))
        row_count += 1
        if row_count == WARMUP_ROWS:
            cols = term_cols()
            print_header(len(dbs), low_hz_g, high_hz_g, center_hz, cols)
            for wdbs, wts in warmup_buf:
                render_row(wdbs, wts, cols)
        continue

    cols = term_cols()
    if (row_count - WARMUP_ROWS) % HEADER_EVERY == 0:
        print_header(len(dbs), low_hz_g, high_hz_g, center_hz, cols)
    render_row(dbs, ts_str, cols)
    row_count += 1
PYEOF

    # rtl_power outputs CSV to stdout; pipe into the Python renderer
    rtl_power \
        -f "${low_hz}:${high_hz}:${bin_hz}" \
        -i "$WATERFALL_INTERVAL" \
        -g "$gain_arg" \
        -d "$DEVICE_INDEX" \
        - 2>/dev/null \
    | python3 "$py_script"

    rm -f "$py_script"
}

# ==========================================
# Entry Point
# ==========================================
main() {
    # Check not root
    if [ "$EUID" -eq 0 ]; then
        log_error "Please run as a regular user (not root)."
        exit 1
    fi

    check_deps
    check_device

    # Ensure log and heartbeat state directories exist
    mkdir -p "$(dirname "$LOG_FILE")"
    mkdir -p "$HB_STATE_DIR"

    print_banner

    # Log session start to file
    {
        echo ""
        echo "========================================"
        echo "Session started: $(date '+%Y-%m-%d %H:%M:%S')"
        echo "Frequency: $FREQ"
        echo "Squelch: $SQUELCH_LEVEL | Gain: $GAIN | Device: $DEVICE_INDEX"
        echo "Decoders: $([ "$MULTIMON_DECODERS" = "ALL" ] && echo "ALL" || echo "$MULTIMON_DECODERS")"
        echo "Heartbeat interval: ${HEARTBEAT_INTERVAL_SECS}s"
        echo "========================================"
    } >> "$LOG_FILE"

    if [ "$WATERFALL" = true ]; then
        run_waterfall
    elif [ "$DIAGNOSE" = true ]; then
        run_diagnose
    elif [ -n "$SINGLE_FREQ" ]; then
        run_monitor_single
    elif [ "$RECORD_AUDIO" = true ]; then
        run_monitor_with_recording
    else
        run_monitor
    fi
}

main "$@"
