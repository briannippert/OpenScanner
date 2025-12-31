import { spawn, ChildProcessWithoutNullStreams, ChildProcess } from 'child_process';
import { EventEmitter } from 'events';
import { Channel, CHANNELS, ScannerState } from '../models';

export class RtlDevice extends EventEmitter {
    private state: ScannerState = {
        status: 'IDLE',
        signalStrength: 0
    };
    private isRunning: boolean = false;
    private scanInterval: NodeJS.Timeout | null = null;
    private activeProcess: ChildProcessWithoutNullStreams | null = null;
    private fmProcess: ChildProcess | null = null;
    private decoderProcess: ChildProcess | null = null;
    private channelIndex: number = 0;
    private sessionTimeout: NodeJS.Timeout | null = null;
    private manualOverride: boolean = false;

    constructor() {
        super();
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

        // Restart scanning
        this.startScanning();
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
        if (this.decoderProcess) {
            this.decoderProcess.kill();
            this.decoderProcess = null;
        }
        if (this.fmProcess) {
            this.fmProcess.kill();
            this.fmProcess = null;
        }
    }

    /**
     * Uses rtl_power to check signal levels at specific frequencies
     * This is more efficient than tuning rtl_fm to every channel
     */
    private async startScanning() {
        if (!this.isRunning || this.manualOverride) return;

        if (this.state.status === 'RECEIVING') {
            // We are receiving, do not scan.
            // The receive loop (lockOn) controls when we return to scanning.
            return;
        }

        this.channelIndex = (this.channelIndex + 1) % CHANNELS.length;
        const channel = CHANNELS[this.channelIndex];

        this.updateState({
            status: 'SCANNING',
            currentFrequency: channel.frequency,
            currentChannel: channel,
            signalStrength: 0,
            isAudioStreaming: false
        });

        try {
            const strength = await this.measureSignalStrength(channel.frequency);
            this.updateState({ signalStrength: this.normalizeDb(strength) });

            // Threshold for "squelch"
            if (strength > -10) {
                this.lockOn(channel);
                return;
            } else {
                // Log periodic scans if strength is above a certain noise floor but below squelch
                if (strength > -14) {
                    console.log(`[Scan] ${channel.alphaTag}: ${strength.toFixed(2)} dB (Below Squelch)`);
                }
            }
        } catch (error) {
            console.warn(`Scanning warning: ${error}`);
            // Don't stop entirely on one error, could be a transient busy device
            // but if it's "tools not found", we might want to know.
            if (error && error.toString().includes('ENOENT')) {
                this.emit('error', 'RTL-SDR tools not found. Please install them.');
                this.stop();
                return;
            }
        }

        // Schedule next scan
        if (this.isRunning && !this.manualOverride) {
            this.scanInterval = setTimeout(() => this.startScanning(), 100) as any;
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

        // 1. Start rtl_fm (Demodulator)
        const fmArgs = [
            '-f', `${channel.frequency}M`,
            '-s', '48000',
            '-p', '0',
            '-' 
        ];

        console.log(`Spawning: rtl_fm ${fmArgs.join(' ')}`);
        this.fmProcess = spawn('rtl_fm', fmArgs);

        // 2. Start dsd-fme (Digital Decoder)
        const dsdArgs = [
            '-i', '-',
            '-o', '-',
            '-f1', // P25 Phase 1
            '-Z'   // Log Payloads (increases verbosity for detection)
        ];
        
        console.log(`Spawning: dsd-fme ${dsdArgs.join(' ')}`);
        this.decoderProcess = spawn('dsd-fme', dsdArgs);

        if (this.fmProcess.stdout && this.decoderProcess.stdin) {
            this.fmProcess.stdout.pipe(this.decoderProcess.stdin);
        }

        // Activity Detection via stderr
        this.decoderProcess.stderr?.on('data', (data) => {
            const output = data.toString();
            // Detect P25 Sync or Voice frames
            // Common patterns: "Sync: +P25p1", "Slot 1", "Voice", "LDU"
            if (output.includes('Sync:') || output.includes('Voice') || output.includes('Slot 1')) {
                this.handleActivity();
            }
        });

        this.fmProcess.on('error', (err) => console.error('rtl_fm error:', err));
        this.decoderProcess.on('error', (err) => console.error('dsd-fme error:', err));

        // CAPTURE AUDIO
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
