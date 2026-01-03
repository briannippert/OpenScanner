import { spawn, ChildProcessWithoutNullStreams, ChildProcess } from 'child_process';
import { EventEmitter } from 'events';
const gpsd = require('node-gpsd');
import fs from 'fs';
import path from 'path';
import { Channel, ScannerState } from '../models';
import { saveTransmission, getAllChannels } from '../db';

export class RtlDevice extends EventEmitter {
    private state: ScannerState = {
        status: 'IDLE',
        signalStrength: 0
    };
    private isRunning: boolean = false;
    private scanInterval: NodeJS.Timeout | null = null;
    private activeProcess: ChildProcessWithoutNullStreams | null = null;
    private decoderProcess: ChildProcess | null = null;
    private channelIndex: number = 0;
    private sessionTimeout: NodeJS.Timeout | null = null;
    private syncTimeout: NodeJS.Timeout | null = null;
    private decodeStartTimer: NodeJS.Timeout | null = null;
    private manualOverride: boolean = false;
    private gpsListener: any = null;
    private channels: Channel[] = [];

    // Recording State
    private currentRecordingFile: string | null = null;
    private recordingStream: fs.WriteStream | null = null;
    private recordingStartTime: number = 0;
    private recordingLockoutUntil: number = 0; // Prevent recording immediately after channel change

    constructor() {
        super();
        this.reloadChannels();
        this.startGpsTracking();
    }

    public reloadChannels() {
        try {
            this.channels = getAllChannels();
            console.log(`[RtlDevice] Loaded ${this.channels.length} channels.`);
        } catch (e) {
            console.error('Failed to load channels:', e);
        }
    }

    public setSquelch(db: number) {
        this.updateState({ squelch: db });
        console.log(`[RtlDevice] Squelch set to ${db}dB`);
        console.log(`[Debug] Checking breakout: Status=${this.state.status}, Manual=${this.manualOverride}, CurrentDB=${this.state.currentSignalDb}`);

        // Live Check: If we are stuck on a signal (RECEIVING) that is now below squelch, resume scanning!
        if (this.state.status === 'RECEIVING' && !this.manualOverride && this.state.currentSignalDb !== undefined) {
             if (this.state.currentSignalDb < db) {
                 console.log(`[RtlDevice] Breakout triggered: ${this.state.currentSignalDb} < ${db}`);
                 this.resumeScan();
             } else {
                 console.log(`[Debug] Signal ${this.state.currentSignalDb} still >= ${db}`);
             }
        }
    }

    private startGpsTracking() {
        try {
            this.gpsListener = new gpsd.Listener({
                port: 2947,
                hostname: 'localhost',
                parse: true
            });

            this.gpsListener.on('TPV', (data: any) => {
                this.updateState({
                    gps: {
                        ...(this.state.gps || { lat: 0, lon: 0, alt: 0, speed: 0, time: '', fix: 0, sats: 0 }),
                        lat: data.lat || 0,
                        lon: data.lon || 0,
                        alt: data.alt || 0,
                        speed: data.speed || 0,
                        time: data.time,
                        fix: data.mode
                    }
                });
            });

            this.gpsListener.on('SKY', (data: any) => {
                const satCount = data.satellites ? data.satellites.length : 0;
                this.updateState({
                    gps: {
                        ...(this.state.gps || { lat: 0, lon: 0, alt: 0, speed: 0, time: '', fix: 0, sats: 0 }),
                        sats: satCount
                    }
                });
            });

            this.gpsListener.connect(() => {
                console.log('Connected to GPSD');
                this.gpsListener.watch();
            });

            this.gpsListener.on('error', (err: any) => {
                // Silently handle if gpsd isn't running yet
            });
        } catch (e) {
            console.warn('GPS tracking initialization failed');
        }
    }

    public start() {
        if (this.isRunning) return;
        this.isRunning = true;
        this.manualOverride = false;
        console.log('Starting RTL-SDR Device Manager...');
        this.startScanning();
    }

    public stop() {
        this.isRunning = false;
        this.manualOverride = false;
        if (this.scanInterval) {
            clearTimeout(this.scanInterval);
            this.scanInterval = null;
        }
        if (this.sessionTimeout) {
            clearTimeout(this.sessionTimeout);
            this.sessionTimeout = null;
        }
        this.killActiveProcess(); // Legacy rtl_power cleanup
        this.stopScanning();      // New rtl_sdr cleanup
        this.stopDecoding();
        this.updateState({ status: 'IDLE', currentFrequency: undefined, currentChannel: undefined, signalStrength: 0 });
    }

    public holdFrequency(frequency: number) {
        const channel = this.channels.find(c => c.frequency === frequency);
        if (!channel) {
            console.error(`Channel not found for frequency: ${frequency}`);
            return;
        }

        console.log(`Manual hold on: ${channel.alphaTag}`);
        this.manualOverride = true;
        this.isRunning = true;

        // Stop any current scanning or decoding
        if (this.scanInterval) {
            clearTimeout(this.scanInterval);
            this.scanInterval = null;
        }
        if (this.sessionTimeout) {
            clearTimeout(this.sessionTimeout);
            this.sessionTimeout = null;
        }
        this.killActiveProcess();
        this.stopDecoding(); // Stop current stream if any

        // Set lockout to prevent recording the switch blip
        this.recordingLockoutUntil = Date.now() + 3000;

        // Force lock on
        this.lockOn(channel);
    }

    public resumeScan() {
        if (!this.manualOverride) return; // Already scanning or idle
        
        console.log('Resuming scan...');
        this.manualOverride = false;
        
        // Stop current hold
        this.stopDecoding();
        if (this.sessionTimeout) {
            clearTimeout(this.sessionTimeout);
            this.sessionTimeout = null;
        }

        // Set lockout
        this.recordingLockoutUntil = Date.now() + 3000;

        // Restart scanning after a short delay to let hardware settle
        setTimeout(() => this.startScanning(), 500);
    }

    public getState(): ScannerState {
        return this.state;
    }

    private updateState(newState: Partial<ScannerState>) {
        this.state = { ...this.state, ...newState };
        this.emit('state-change', this.state);
    }

    private killActiveProcess() {
        if (this.activeProcess) {
            this.activeProcess.kill();
            this.activeProcess = null;
        }
    }

    private stopDecoding() {
        if (this.decoderProcess && this.decoderProcess.pid) {
            try {
                // Kill the entire process group (minus sign on PID)
                process.kill(-this.decoderProcess.pid, 'SIGTERM');
            } catch (e) {
                // Process might already be dead
            }
            this.decoderProcess = null;
        }
    }

    private scannerProcess: ChildProcessWithoutNullStreams | null = null;
    private scanBuffer: Buffer = Buffer.alloc(0);
    private lastScanUpdate: number = 0;

    /**
     * Uses rtl_sdr to stream I/Q samples and performs FFT in Node.js
     * Allows for 10Hz+ update rates.
     */
    private async startScanning() {
        if (!this.isRunning || this.manualOverride || this.state.status === 'RECEIVING') return;

        if (this.channels.length === 0) {
            console.warn('[RtlDevice] No channels to scan.');
            this.updateState({ status: 'IDLE' });
            return;
        }

        // Calculate Bandwidth and Center Frequency
        const freqs = this.channels.map(c => c.frequency);
        const minFreq = Math.min(...freqs);
        const maxFreq = Math.max(...freqs);
        // Center frequency
        const centerFreq = (minFreq + maxFreq) / 2;
        
        // Use 2.048 MSPS (standard stable rate)
        const sampleRate = 2048000;
        
        // Verify channels fit
        if ((maxFreq - minFreq) > (sampleRate / 1000000 * 0.8)) {
             console.warn('Channels spread too wide for single tuner bandwidth. Scanning first group only.');
        }

        this.updateState({
            status: 'SCANNING',
            currentFrequency: undefined,
            currentChannel: undefined,
            signalStrength: 0,
            isAudioStreaming: false
        });

        // Kill any existing scanner
        if (this.scannerProcess) this.scannerProcess.kill();

        const cmd = 'rtl_sdr';
        // -f frequency (Hz), -s sample_rate (Hz), -g gain (0=auto? No, use fixed for consistency or auto), -n (samples to read, omitted for stream)
        const args = ['-f', Math.floor(centerFreq * 1000000).toString(), '-s', sampleRate.toString(), '-g', '40', '-'];
        
        console.log(`Starting Fast Scan: ${centerFreq} MHz @ ${sampleRate} SPS`);
        this.scannerProcess = spawn(cmd, args);

        let errorOutput = '';
        this.scannerProcess.stderr.on('data', (d) => {
            const msg = d.toString();
            errorOutput += msg;
            if (msg.includes('No supported devices found')) {
                this.emit('error', 'RTL-SDR Hardware Not Detected. Please check your USB connection.');
                this.stop();
            }
        });

        this.scannerProcess.stdout.on('data', (chunk: Buffer) => {
            this.processScanData(chunk, centerFreq, sampleRate);
        });

        this.scannerProcess.on('close', (code) => {
            if (this.isRunning && this.state.status === 'SCANNING') {
                if (code !== 0 && errorOutput.includes('usb_open error')) {
                     this.emit('error', 'RTL-SDR USB Permission Error. Try running with sudo or check udev rules.');
                }
                console.log(`Scanner process died with code ${code}, restarting in 5s...`);
                setTimeout(() => this.startScanning(), 5000);
            }
        });
    }

    private processScanData(chunk: Buffer, centerFreq: number, sampleRate: number) {
        // Rate Limit Updates (15Hz max)
        const now = Date.now();
        if (now - this.lastScanUpdate < 60) return; // Skip data if too fast
        this.lastScanUpdate = now;

        // We need 256 samples (512 bytes) for a quick snapshot
        // Use the LAST 512 bytes to get the most recent data (lowest latency)
        if (chunk.length < 512) return;
        
        const fftSize = 256;
        const samples = chunk.slice(chunk.length - (fftSize * 2)); // Last 512 bytes
        
        // Simple DFT/FFT for Power Spectrum
        // Since we only need magnitudes for specific channels, we could optimize,
        // but a full low-res spectrum is nice for the UI.
        const spectrum = this.computePowerSpectrum(samples, fftSize);
        
        // Map bins to frequencies
        const rfSpectrum = spectrum.map((db, i) => {
            // Bin 0 = Center - Rate/2
            // Bin N/2 = Center
            // Bin N = Center + Rate/2
            // Actually, standard FFT layout: 0 is DC (Center).
            // But usually we shift it. Let's assume standard shifted output:
            // i ranges 0 to fftSize. 
            // Freq = Center + (i - fftSize/2) * (Rate / fftSize)
            const freqOffset = (i - fftSize / 2) * (sampleRate / fftSize);
            return {
                frequency: (centerFreq * 1000000 + freqOffset) / 1000000,
                db: db
            };
        });

        // Update State
        this.updateState({ rfSpectrum });

        // Check Channels
        let bestChannel: Channel | null = null;
        let maxDetectedDb = -100;
        const threshold = this.state.squelch ?? -40;

        for (const channel of this.channels) {
            // Find bin
            const freqDiff = channel.frequency - centerFreq; // MHz
            const binIndex = Math.floor((freqDiff * 1000000 / sampleRate) * fftSize + fftSize/2);
            
            if (binIndex >= 0 && binIndex < fftSize) {
                const db = spectrum[binIndex];
                if (db > maxDetectedDb) maxDetectedDb = db;
                
                if (db > threshold && (bestChannel === null || db > maxDetectedDb)) {
                    bestChannel = channel;
                }
            }
        }

        this.updateState({ currentSignalDb: maxDetectedDb });

        if (bestChannel) {
             this.stopScanning(); // Kill rtl_sdr
             this.recordingLockoutUntil = Date.now() + 3000;
             this.lockOn(bestChannel);
        }
    }

    private computePowerSpectrum(buffer: Buffer, size: number): number[] {
        // Simple DFT (Direct Fourier Transform) - O(N^2) but N=256 is tiny.
        // Input: buffer (Uint8) 0-255. Center 127.5.
        // Output: dB array
        
        const signalReal = new Float32Array(size);
        const signalImag = new Float32Array(size);
        
        for (let i = 0; i < size; i++) {
            signalReal[i] = (buffer[i*2] - 127.5) / 127.5;
            signalImag[i] = (buffer[i*2+1] - 127.5) / 127.5;
        }

        const magnitudes = new Float32Array(size);
        
        // Apply Hanning Window to reduce leakage
        for (let i = 0; i < size; i++) {
            const multiplier = 0.5 * (1 - Math.cos((2 * Math.PI * i) / (size - 1)));
            signalReal[i] *= multiplier;
            signalImag[i] *= multiplier;
        }

        // Compute FFT (actually using DFT for simplicity without external lib)
        // For N=256, 65k iterations is < 1ms in JS.
        // We want shifted output (0Hz in middle).
        
        const output = new Array(size).fill(-100);

        for (let k = 0; k < size; k++) { // For each output bin
            let sumReal = 0;
            let sumImag = 0;
            const angleTerm = -2 * Math.PI * k / size;
            
            for (let n = 0; n < size; n++) { // Sum over time samples
                 // Euler: e^(-ix) = cos(x) - i*sin(x)
                 const angle = angleTerm * n;
                 const c = Math.cos(angle);
                 const s = Math.sin(angle);
                 
                 // (a+bi)(c+di) = (ac - bd) + i(ad + bc)
                 // Here (signalReal + i signalImag) * (cos + i sin) -> wait, formula is e^-i...
                 // e^-ix = cos(x) - i sin(x)
                 // So we multiply by (cos - i sin)
                 // (r + ji) * (c - js) = (rc + rs) + j(ic - rs) -- wait.
                 // (r + ji)(c - js) = rc - jrs + jic - j^2 is 
                 // rc + s*r(imag part?) No.
                 // Real: r*c - i*(-s) = rc + is
                 // Imag: r*(-s) + i*c = ic - rs
                 
                 sumReal += signalReal[n] * c + signalImag[n] * s; // check sign?
                 sumImag += signalImag[n] * c - signalReal[n] * s;
            }
            
            // Power = Real^2 + Imag^2
            const power = sumReal*sumReal + sumImag*sumImag;
            const db = 10 * Math.log10(power + 1e-9); // Prevent log0
            magnitudes[k] = db; 
        }

        // Shift: Swap left and right halves to put DC in middle
        const shifted = [
            ...magnitudes.slice(size / 2),
            ...magnitudes.slice(0, size / 2)
        ];

        // Normalize dB (empirical adjustment for RTL-SDR range)
        return shifted.map(x => x - 20); // Arbitrary offset to match typical "noise floor" ~-40dB
    }

    private stopScanning() {
        if (this.scannerProcess) {
            this.scannerProcess.stdout.removeAllListeners();
            this.scannerProcess.stderr.removeAllListeners();
            this.scannerProcess.removeAllListeners(); // Prevent restart loop
            this.scannerProcess.kill('SIGKILL'); // Force kill
            this.scannerProcess = null;
        }
    }


    private normalizeDb(db: number): number {
        // Map -60dB (noise) to 0% and -10dB (strong) to 100%
        const min = -60;
        const max = -10;
        let percent = ((db - min) / (max - min)) * 100;
        return Math.max(0, Math.min(100, percent));
    }

    private measureSignalStrength(freqMhz: number): Promise<number> {
        return new Promise((resolve, reject) => {
            // rtl_power requires a valid range (lower < upper)
            // Create a small window around the target frequency (+/- 5kHz)
            const offset = 0.005; 
            const lower = (freqMhz - offset).toFixed(4);
            const upper = (freqMhz + offset).toFixed(4);
            
            const cmd = 'rtl_power';
            // args: frequency range, gain (auto), integration time (exit after 1s)
            // Removed -g 50 to use automatic gain
            const args = ['-f', `${lower}M:${upper}M:1k`, '-i', '1', '-1'];

            const proc = spawn(cmd, args);
            this.activeProcess = proc; // Track it so we can kill it
            let output = '';

            proc.stdout.on('data', (data) => {
                output += data.toString();
            });

            proc.on('error', (err) => {
                reject(err.message);
            });

            proc.on('close', (code) => {
                this.activeProcess = null;
                if (code !== 0) {
                    // It might fail if device is busy or not found
                    reject(`rtl_power exited with code ${code}`);
                    return;
                }
                // Output format: date, time, Hz low, Hz high, Hz step, samples, db, db, db...
                const parts = output.trim().split(',');
                if (parts.length > 6) {
                    // Average all dB bins
                    let sum = 0;
                    let count = 0;
                    for (let i = 6; i < parts.length; i++) {
                        const val = parseFloat(parts[i]);
                        if (!isNaN(val)) {
                            sum += val;
                            count++;
                        }
                    }
                    const avg = count > 0 ? sum / count : -100;
                    resolve(avg);
                } else {
                    resolve(-100); // Invalid reading
                }
            });
        });
    }

    private activityTimer: NodeJS.Timeout | null = null;

    private lockOn(channel: Channel) {
        if (this.scanInterval) {
            clearTimeout(this.scanInterval);
            this.scanInterval = null;
        }

        console.log(`[${new Date().toLocaleTimeString()}] ${this.manualOverride ? 'Manual Hold' : 'Locked on'} to ${channel.alphaTag} (${channel.frequency} MHz)`);
        
        // Initial state: 
        // If manual hold, we are MONITORING (listening for signal).
        // If auto scan, we assume we found a signal, so RECEIVING (but will fallback if DSD finds nothing).
        this.updateState({
            status: this.manualOverride ? 'MONITORING' : 'RECEIVING',
            currentFrequency: channel.frequency,
            currentChannel: channel,
            isAudioStreaming: true
        });

        // Start the decoding pipeline
        this.startDecoding(channel);

        // Safety timeout only if NOT manual override
        if (!this.manualOverride) {
            // Stop listening after 10 seconds and return to scan
            this.sessionTimeout = setTimeout(() => {
                console.log('Session timeout, resuming scan...');
                this.stopDecoding();
                this.startScanning();
            }, 10000);
        }
    }

    private startDecoding(channel: Channel) {
        this.stopDecoding(); // Ensure clean slate

        // Stable shell pipe mode
        // Force P25 Phase 1 (-f1)
        const command = `rtl_fm -f ${channel.frequency}M -s 48000 -g 45 -p 0 -M fm - | dsd-fme -f1 -i - -o - -s 48000`;

        console.log(`Executing Stable Pipeline: ${command}`);
        
        // Wait 500ms for hardware to release
        setTimeout(() => {
            if (!this.isRunning) return;
            
            this.decoderProcess = spawn('sh', ['-c', command], { detached: true });
            
            this.decoderProcess.on('error', (err) => {
                console.error('Failed to start decoder pipeline:', err.message);
            });

            this.decoderProcess.on('exit', (code) => {
                if (code !== null && code !== 0) {
                    console.log(`[PIPELINE] Process exited with code ${code}`);
                }
            });

            // Activity Detection via stderr
            this.decoderProcess.stderr?.on('data', (data) => {
                const output = data.toString().trim();
                if (output && !output.includes('██') && !output.includes('Version')) {
                    console.log(`[DSD] ${output}`);
                }
                if (output.includes('Sync:') || output.includes('Voice') || output.includes('P25')) {
                    this.handleActivity();
                }
            });

            // CAPTURE AUDIO from the end of the pipe (dsd-fme's stdout)
            this.decoderProcess.stdout?.on('data', (chunk) => {
                if (chunk.length > 0) {
                    // console.log(`[Audio] Chunk: ${chunk.length} bytes`); // Uncomment for heavy debugging
                }
                if (this.recordingStream) {
                    this.recordingStream.write(chunk);
                }
                this.emit('audio', chunk);
            });
        }, 300);
    }

    private handleActivity() {
        // If we detect valid P25 frames, set state to RECEIVING
        if (this.state.status !== 'RECEIVING') {
            this.updateState({ status: 'RECEIVING' });
            this.startRecording();
        }

        // Reset inactivity timer
        if (this.activityTimer) clearTimeout(this.activityTimer);
        
        // Reset session timeout (keep listening if active)
        if (this.sessionTimeout && !this.manualOverride) {
            clearTimeout(this.sessionTimeout);
            this.sessionTimeout = setTimeout(() => {
                console.log('Session timeout (post-activity), resuming scan...');
                this.stopDecoding();
                this.startScanning();
            }, 5000); // 5s hang time after transmission ends
        }

        // If no more activity for 2 seconds, revert state
        this.activityTimer = setTimeout(() => {
            this.stopRecording();
            if (this.manualOverride) {
                this.updateState({ status: 'MONITORING' });
            } 
            // If auto-scan, the sessionTimeout will handle the exit eventually
        }, 2000);
    }

    private startRecording() {
        if (this.recordingStream || !this.state.currentChannel) return;
        
        // Skip if within lockout period (prevent switch blips)
        if (Date.now() < this.recordingLockoutUntil) {
            return;
        }

        const filename = `rec_${Date.now()}_${this.state.currentChannel.frequency}.raw`;
        const dir = path.join(__dirname, '../../data/recordings');
        if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
        
        this.currentRecordingFile = filename;
        this.recordingStartTime = Date.now();
        this.recordingStream = fs.createWriteStream(path.join(dir, filename));
        
        console.log(`⏺️ Starting recording: ${filename}`);
    }

    private stopRecording() {
        if (!this.recordingStream || !this.state.currentChannel) return;

        const duration = (Date.now() - this.recordingStartTime) / 1000;
        const file = this.currentRecordingFile;
        const filePath = path.join(__dirname, '../../data/recordings', file!);
        
        this.recordingStream.end();
        this.recordingStream = null;
        this.currentRecordingFile = null;

        // Save to DB (only if duration > 2s to skip noise and channel switching blips)
        if (duration > 2.0) {
            const entry = {
                id: `log_${Date.now()}`,
                timestamp: new Date().toISOString(),
                frequency: this.state.currentChannel.frequency,
                alphaTag: this.state.currentChannel.alphaTag,
                description: this.state.currentChannel.description,
                lat: this.state.gps?.lat,
                lon: this.state.gps?.lon,
                alt: this.state.gps?.alt,
                audio_path: file!,
                duration: duration
            };

            saveTransmission(entry);
            console.log(`💾 Saved transmission: ${duration.toFixed(1)}s`);
            
            // Broadcast new log to all clients
            this.emit('new-log', entry);
        } else {
            // Delete the tiny file to save space and keep it tidy
            try {
                if (fs.existsSync(filePath)) fs.unlinkSync(filePath);
            } catch (e) {
                // Ignore errors
            }
        }
    }
}
