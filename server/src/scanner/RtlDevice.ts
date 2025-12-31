import { spawn, ChildProcessWithoutNullStreams, ChildProcess } from 'child_process';
import { EventEmitter } from 'events';
const gpsd = require('node-gpsd');
import { Channel, CHANNELS, ScannerState } from '../models';

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
    private manualOverride: boolean = false;
    private gpsListener: any = null;

    constructor() {
        super();
        this.startGpsTracking();
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
        this.killActiveProcess();
        this.stopDecoding();
        this.updateState({ status: 'IDLE', currentFrequency: undefined, currentChannel: undefined, signalStrength: 0 });
    }

    public holdFrequency(frequency: number) {
        const channel = CHANNELS.find(c => c.frequency === frequency);
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

    private scanCounter: number = 0;

    /**
     * Uses rtl_power to check signal levels at specific frequencies
     * Optimized: Scans all channels simultaneously if they fit in one bandwidth chunk.
     */
    private async startScanning() {
        if (!this.isRunning || this.manualOverride) return;

        if (this.state.status === 'RECEIVING') {
            return;
        }

        // Calculate Bandwidth needed
        const freqs = CHANNELS.map(c => c.frequency);
        const minFreq = Math.min(...freqs);
        const maxFreq = Math.max(...freqs);
        const bandwidth = maxFreq - minFreq;

        // Add margins (0.1 MHz)
        const scanStart = Math.floor((minFreq - 0.1) * 10) / 10; // Round down to 100k
        const scanEnd = Math.ceil((maxFreq + 0.1) * 10) / 10;    // Round up to 100k
        
        // If channels fit in 2MHz, do a group scan (fastest)
        if (scanEnd - scanStart < 2.0) {
            this.updateState({
                status: 'SCANNING',
                currentFrequency: undefined, // Show 'Scanning'
                currentChannel: undefined,
                signalStrength: 0,
                isAudioStreaming: false
            });

            try {
                // Scan range with 10k bins (narrow enough for P25)
                // -i 1s is default/stable. We could try 0.5s if supported, but 1s is safe.
                const spectrum = await this.performSpectrumSweep(`${scanStart}M`, `${scanEnd}M`, '10k');
                this.updateState({ rfSpectrum: spectrum });

                // Check for hits
                let bestChannel: Channel | null = null;
                let bestStrength = -100;

                for (const channel of CHANNELS) {
                    // Find bin for this frequency
                    // Simple closest match
                    const hit = spectrum.reduce((prev, curr) => 
                        Math.abs(curr.frequency - channel.frequency) < Math.abs(prev.frequency - channel.frequency) ? curr : prev
                    );

                    if (hit && Math.abs(hit.frequency - channel.frequency) < 0.02) { // Match within 20kHz
                        const db = hit.db;
                        // Squelch check: -25dB is more sensitive
                        if (db > -25 && db > bestStrength) {
                            bestStrength = db;
                            bestChannel = channel;
                        }
                    }
                }

                if (bestChannel) {
                    this.lockOn(bestChannel);
                    return;
                }

            } catch (error) {
                console.warn(`Group Scan warning: ${error}`);
                if (error && error.toString().includes('ENOENT')) {
                    this.emit('error', 'RTL-SDR tools not found. Please install them.');
                    this.stop();
                    return;
                }
            }
        } else {
            // Fallback: Round Robin (Legacy Logic) for wide spreads
            // (Omitted for brevity as user only has 2 close channels)
            console.warn('Channels spread too wide for fast scan. Only scanning first group.');
        }

        // Schedule next scan immediately (loop)
        if (this.isRunning && !this.manualOverride) {
            this.scanInterval = setTimeout(() => this.startScanning(), 50) as any;
        }
    }

    private performSpectrumSweep(lower: string, upper: string, bin: string): Promise<{ frequency: number, db: number }[]> {
        return new Promise((resolve, reject) => {
            const cmd = 'rtl_power';
            const args = ['-f', `${lower}:${upper}:${bin}`, '-i', '1', '-1'];
            const proc = spawn(cmd, args);
            let output = '';

            proc.stdout.on('data', (data) => output += data.toString());
            
            // Handle stderr if needed, but usually we just want stdout
            
            proc.on('close', (code) => {
                if (code !== 0) {
                    console.error(`rtl_power failed with code ${code}. Output: ${output.substring(0, 100)}...`);
                    return reject('rtl_power failed');
                }
                
                const lines = output.split('\n');
                const csvLine = lines.find(line => line.includes(',') && line.split(',').length > 6);
                
                if (csvLine) {
                    const parts = csvLine.trim().split(',');
                    const startFreq = parseFloat(parts[2]);
                    const endFreq = parseFloat(parts[3]);
                    const step = parseFloat(parts[4]);
                    
                    const spectrum = [];
                    
                    // Column 6 is "samples", db values start at index 6 in 0-based array? 
                    // No, format is: date(0), time(1), low(2), high(3), step(4), samples(5), db(6)...
                    
                    for (let i = 6; i < parts.length; i++) {
                        const db = parseFloat(parts[i]);
                        if (!isNaN(db)) {
                            // Calculate frequency for this bin
                            const freq = startFreq + ((i - 6) * step);
                            spectrum.push({ frequency: freq / 1000000, db }); // Convert to MHz
                        }
                    }
                    // console.log(`Spectrum generated: ${spectrum.length} points`);
                    resolve(spectrum);
                } else {
                    console.warn("rtl_power: No valid CSV line found in output.");
                    resolve([]);
                }
            });
        });
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

        // Use dsd-fme's built-in RTL-SDR support directly
        // -i rtl:0 = device 0, -c = center freq, -ft = target freq, -O 8000 = 8k output
        const command = `dsd-fme -i rtl:0 -c ${channel.frequency}M -ft ${channel.frequency}M -g 45 -o - -O 8000 -fp`;

        console.log(`Executing Direct Decoder: ${command}`);
        this.decoderProcess = spawn('sh', ['-c', command], { detached: true });
        
        this.decoderProcess.on('error', (err) => {
            console.error('Failed to start decoder pipeline:', err.message);
        });

        this.decoderProcess.on('exit', (code) => {
            console.log(`[PIPELINE] Process exited with code ${code}`);
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
            this.emit('audio', chunk);
        });
    }

    private handleActivity() {
        // If we detect valid P25 frames, set state to RECEIVING
        if (this.state.status !== 'RECEIVING') {
            this.updateState({ status: 'RECEIVING' });
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
            if (this.manualOverride) {
                this.updateState({ status: 'MONITORING' });
            } 
            // If auto-scan, the sessionTimeout will handle the exit eventually
        }, 2000);
    }
}
