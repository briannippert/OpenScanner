# OpenScanner Roadmap & Feature Wishlist

## ✅ Recently Completed
- [x] **P25 Phase 1 Decoding:** Enforced `-f1` flag for stable digital decoding.
- [x] **Audio Pipeline:** Standardized on 48kHz sampling rate (Server & Client).
- [x] **GPS Integration:** Real-time location tracking and GPS-synced clock.
- [x] **Hardware Safety:** Automatic detection of RTL-SDR disconnection with frontend alerts.
- [x] **UX Polish:** Screen Wake Lock to prevent sleep during scanning.

---

## 🛠 Core Radio Capabilities
- [ ] **Multi-Mode Support:** 
    - Allow per-channel configuration for **DMR**, **NXDN**, and **Analog FM**.
    - Dynamically switch `dsd-fme` flags based on the channel type.
- [ ] **P25 Trunking (P25RX approach):**
    - Instead of scanning frequencies, tune to a Control Channel (CC).
    - Decode CC data to follow Talkgroups (TGIDs) dynamically.
- [ ] **Multiple SDR Support:**
    - Use one SDR for the Control Channel and a second SDR for Voice Channel following.
    - Essential for rapid trunking response without missing calls.
- [ ] **Sub-Audible Squelch (CTCSS/DCS):**
    - Implement PL tone filtering for Analog FM channels to reduce interference.

## 🎧 Audio & Intelligence
- [ ] **Transcription (Speech-to-Text):**
    - Integrate `whisper.cpp` or a local STT engine to transcribe calls in real-time.
    - Searchable text logs for all transmissions.
- [ ] **Audio Compression:**
    - Encode recordings to **MP3** or **Opus** instead of raw WAV/PCM to save disk space.
- [ ] **Silence Removal:**
    - Post-process recordings to trim dead air at the start/end of transmissions.
- [ ] **Metadata Tagging:**
    - Embed Frequency, Talkgroup, and Timestamp directly into the audio file metadata.

## 💻 User Interface (Client)
- [ ] **Talkgroup Management:**
    - Interface to alias TGIDs (e.g., "1001" -> "Main Dispatch") and filter them.
- [ ] **Map Visualization:**
    - Plot recording locations on a map (Leaflet/OpenLayers) to see where calls were received.
- [ ] **Advanced Spectrum View:**
    - Add Zoom/Pan controls to the waterfall.
    - Click-to-tune functionality on the spectrum display.
- [ ] **Mobile PWA:**
    - Add a manifest and service worker to allow "Install to Home Screen" on phones.

## ⚙️ System & Backend
- [ ] **Configuration File:**
    - Move hardcoded settings (Gain, PPM, Squelch defaults) to a `config.json` or `.env` file.
- [ ] **Docker Containerization:**
    - Create a `Dockerfile` that bundles `rtl-sdr`, `dsd-fme`, and the Node app for easy deployment.
- [ ] **MQTT / Home Assistant Integration:**
    - Publish "Call Received" events to MQTT triggers (e.g., pause TV when police radio is active).
- [ ] **Disk Management:**
    - Auto-delete old recordings when disk usage hits a threshold (e.g., "Keep last 5GB").

## 🐛 Known Issues / Optimization
- [ ] **Squelch Hysteresis:** Improve logic to prevent rapid toggling on weak signals.
- [ ] **Jitter Buffer:** Refine frontend audio buffering for smoother playback over poor WiFi.
