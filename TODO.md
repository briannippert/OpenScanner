# OpenScanner Project TODO List

## Current Status
- **Scanning:** Functional. Uses `rtl_power` to detect signal peaks.
- **Tuning:** Functional. "Locks on" to frequencies with signal strength > -5dB.
- **Frontend:** Visuals are working. Audio player logic implemented (expects 8k PCM).
- **Backend:** Spawns `rtl_fm | dsd-fme` pipeline and streams binary audio over WebSocket.

## Missing Features / Next Steps

### 1. Installation Verification
- [x] **Code Implementation:** Done.
- [x] **System Dependencies:**
    - `dsd-fme` is installed at `/usr/local/bin/dsd-fme`.
    - `rtl_fm` is installed at `/usr/bin/rtl_fm`.

### 2. Audio Tuning
- [ ] **Sample Rate Matching:**
    - Current code assumes `dsd-fme` outputs 8000Hz 16-bit PCM.
    - Needs verification once a real transmission is captured.
- [ ] **Jitter Buffer:**
    - Frontend implementation is very basic. If audio is choppy, we need a better buffering strategy.

### 3. Stability & Features
- [ ] **Squelch Logic Improvements:**
    - Observe if -10dB is a reliable threshold for real transmissions.
- [ ] **Activity Detection:**
    - Currently uses a 10s hard timeout. Ideally, use `dsd-fme` logs to keep the channel open while voice is active.
- [ ] **Volume Control:**
    - Add a volume slider to the frontend.