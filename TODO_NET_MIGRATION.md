# .NET Core Migration Plan

## Objective
Rewrite the `OpenScanner` backend (currently Node.js) in **.NET 8/9** to improve performance (FFT/DSP), stability (Process management), and type safety.

## 1. Project Setup
- [x] Install .NET SDK (8.0 or later).
- [x] Create solution: `dotnet new sln -n OpenScanner`
- [x] Create Web API: `dotnet new webapi -n OpenScanner.Server`
- [x] Add packages:
    - `FftSharp` (or `MathNet.Numerics`) for Spectrum Analysis.
    - `System.IO.Ports` (if needed for serial, though likely not for this project).

## 2. Core Models (Port from `models.ts`)
- [x] Create `ScannerState` record.
- [x] Create `Channel` record.
- [x] Create `CallLog` record.
- [x] Define shared interfaces for the hardware driver (Mock vs Real).

## 3. Data Persistence (Port from `db.ts`)
- [x] Implement `IChannelRepository` (Implemented via SQLite/Dapper).
- [x] Implement `ICallLogRepository` (Implemented via SQLite/Dapper).

## 4. Hardware Interface (The "Engine")
- [x] Create `ScannerService` (BackgroundService).
    - [x] **Process Management:** Wrapper around `System.Diagnostics.Process` to run `rtl_sdr`, `rtl_fm`, `dsd-fme`.
    - [x] **Stdout Parsing:** Efficiently read binary stdout for `rtl_sdr` (Spectrum) and `dsd-fme` (Audio).
    - [x] **DSP Logic:** 
        - [x] Buffer `rtl_sdr` I/Q samples.
        - [x] Perform FFT using `FftSharp`.
        - [x] Compute Power Spectrum (dB).
    - [x] **State Machine:** Handle transitions (IDLE -> SCANNING -> RECEIVING).

## 5. Web API & Realtime
- [x] **REST Endpoints** (Minimal APIs in `Program.cs`):
    - `GET /api/channels`
    - `POST /api/channels`
    - `POST /api/control` (Start/Stop/Squelch)
- [x] **WebSockets / SignalR**:
    - [x] Broadcast `ScannerState` updates (frequency, signal strength).
    - [x] Broadcast **Audio Stream** (Binary blobs).
    - [x] Broadcast **Spectrum Data** (Float arrays).

## 6. Compatibility Check
- [x] Ensure API routes match the React client *exactly*.
- [x] Ensure WebSocket message format matches exactly (JSON types).

## 7. Deployment
- [ ] Create `Dockerfile` for .NET app.
- [ ] Create `install_service.sh` for systemd integration.