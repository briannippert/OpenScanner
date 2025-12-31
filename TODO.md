# OpenScanner Project TODO List

## Current Status
- **Scanning:** Functional. Uses `rtl_power` to detect signal peaks.
- **Tuning:** Functional. "Locks on" to frequencies with signal strength > -5dB.
- **Frontend:** Visuals are working. Audio player logic implemented (expects 8k PCM).
- **Backend:** Spawns `rtl_fm | dsd-fme` pipeline and streams binary audio over WebSocket.

## Missing Features / Next Steps

### 1. Installation Verification
- [x] **Code Implementation:** Done.
- [ ] **System Dependencies:**
    - **CRITICAL:** `dsd-fme` is MISSING from the system.
    - User must install `dsd-fme`.
    - User must ensure `rtl_fm` is installed (it appears to be).

### 2. Audio Tuning
- [ ] **Sample Rate Matching:**
    - Current code assumes `dsd-fme` outputs 8000Hz 16-bit PCM.
    - Needs verification once `dsd-fme` is running.
- [ ] **Jitter Buffer:**
    - Frontend implementation is very basic. Might drift or click.
    - Consider a proper ring buffer if audio quality is poor.

### 3. Stability
- [ ] **Process Zombie Check:**
    - Ensure `rtl_fm` and `dsd-fme` are truly killed when scanning resumes.
    - If `dsd-fme` hangs, it might block the device.

### 4. Code Improvements
- [ ] **Squelch Logic:**
    - Currently uses a hard 10s timeout for listening.
    - Ideally, read `dsd-fme` stderr to detect "Sync: +P25p1" messages and extend timeout while sync is present.