# OpenScanner P25

OpenScanner is a web-based P25 digital radio scanner designed for the Raspberry Pi. It provides a modern, responsive UI for monitoring local public safety frequencies using an RTL-SDR dongle.

![OpenScanner UI](https://via.placeholder.com/800x400?text=OpenScanner+P25+Dashboard)

## 🚀 Features

- **P25 Digital Decoding**: Real-time decoding of P25 Phase 1 digital voice.
- **Web-Based Dashboard**: Access your scanner from any device on your network.
- **Live Waterfall**: Visual scrolling spectrogram of decoded audio.
- **GPS Integration**: Displays live location, altitude, speed, and satellite count.
- **Transmission Logging**: Automatic logging of active voice calls.
- **Service-Ready**: Includes a dedicated installer to run OpenScanner as a system service on port 80.

## 🛠 Hardware Requirements

- **Raspberry Pi**: (Pi 4 or Pi 5 recommended).
- **RTL-SDR Dongle**: (RTL2832U based, such as RTLSDRv3 or v4).
- **GPS Receiver**: (Optional, any NMEA-compatible USB GPS receiver like u-blox).
- **Antenna**: Tuned for your local 150-800MHz public safety bands.

## 📦 Installation

### 1. Clone the repository
```bash
git clone https://github.com/briannippert/OpenScanner.git
cd OpenScanner
```

### 2. Install Hardware Dependencies
Run the included script to build and install `dsd-fme`, `mbelib`, and `rtl-sdr` tools:
```bash
chmod +x install_dsd.sh
./install_dsd.sh
```

### 3. Install as a System Service
OpenScanner can be installed to run automatically at boot on port 80:
```bash
chmod +x install_service.sh
sudo ./install_service.sh
```

## 🖥 Usage

Once installed as a service, access the dashboard at:
`http://<your-pi-ip>`

### Manual Controls:
- **Click a Channel**: Hold the scanner on a specific frequency.
- **Resume Scan**: Return to scanning all configured channels.
- **Audio Activation**: Browsers block audio by default; click anywhere on the page once it loads to enable sound.

## 🔧 Configuration

### Adding Channels
Edit `server/src/models.ts` to add your local frequencies:
```typescript
{
    frequency: 155.0325,
    alphaTag: "Local Police",
    description: "Dispatch",
    mode: "P25"
}
```
After editing, re-run `sudo ./install_service.sh` to apply changes.

## 📜 Acknowledgments

OpenScanner leverages these amazing open-source projects:
- [DSD-FME](https://github.com/lwvmobile/dsd-fme) - Digital Speech Decoder (Florida Man Edition)
- [rtl-sdr](https://osmocom.org/projects/rtl-sdr/wiki/Rtl-sdr) - RTL2832-based SDR driver
- [gpsd](https://gpsd.gitlab.io/gpsd/) - GPS Service Daemon
