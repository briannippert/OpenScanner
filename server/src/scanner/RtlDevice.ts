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

    constructor() {
        super();
    }

    public start() {
        if (this.isRunning) return;
        this.isRunning = true;
        console.log('Starting RTL-SDR Device Manager...');
        this.startScanning();
    }

    public stop() {
        this.isRunning = false;
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
        if (!this.isRunning) return;

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

            // Threshold for "squelch" (e.g., -5dB seems to be a good cutoff above the -13dB noise floor)
            if (strength > -5) {
                this.lockOn(channel);
                return; // lockOn will restart scanning when done
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
        if (this.isRunning) {
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

    private lockOn(channel: Channel) {
        if (this.scanInterval) {
            clearTimeout(this.scanInterval);
            this.scanInterval = null;
        }

        console.log(`[${new Date().toLocaleTimeString()}] Locked on to ${channel.alphaTag} (${channel.frequency} MHz)`);
        
        this.updateState({
            status: 'RECEIVING',
            currentFrequency: channel.frequency,
            currentChannel: channel,
            isAudioStreaming: true
        });

        // Start the decoding pipeline
        this.startDecoding(channel);

        // Safety timeout: Stop listening after 10 seconds and return to scan
        // In a real app, this would be reset by activity (audio detected)
        this.sessionTimeout = setTimeout(() => {
            console.log('Session timeout, resuming scan...');
            this.stopDecoding();
            this.startScanning();
        }, 10000);
    }

    private startDecoding(channel: Channel) {
        this.stopDecoding(); // Ensure clean slate

        // 1. Start rtl_fm (Demodulator)
        // rtl_fm -f 155.0325M -s 48k -p 0 - | ...
        const fmArgs = [
            '-f', `${channel.frequency}M`,
            '-s', '48000', // 48k sample rate required for DSD
            '-p', '0',     // ppm error
            '-'            // Output to stdout
        ];

        console.log(`Spawning: rtl_fm ${fmArgs.join(' ')}`);
        this.fmProcess = spawn('rtl_fm', fmArgs);

        // 2. Start dsd-fme (Digital Decoder)
        // ... | dsd-fme -i - -o - (Input from stdin, Output to stdout)
        // -f1 for P25 Phase 1 (optional, auto is default usually)
        const dsdArgs = [
            '-i', '-',
            '-o', '-',
            '-f1' // Force P25 Phase 1 for now based on user data
        ];
        
        console.log(`Spawning: dsd-fme ${dsdArgs.join(' ')}`);
        this.decoderProcess = spawn('dsd-fme', dsdArgs);

        // Pipe rtl_fm -> dsd-fme
        if (this.fmProcess.stdout && this.decoderProcess.stdin) {
            this.fmProcess.stdout.pipe(this.decoderProcess.stdin);
        }

        // Handle errors
        this.fmProcess.stderr?.on('data', (d) => { /* console.log(`rtl_fm stderr: ${d}`); */ });
        this.decoderProcess.stderr?.on('data', (d) => { /* console.log(`dsd stderr: ${d}`); */ });

        this.fmProcess.on('error', (err) => console.error('rtl_fm error:', err));
        this.decoderProcess.on('error', (err) => console.error('dsd-fme error:', err));

        // CAPTURE AUDIO
        // dsd-fme stdout should be the decoded audio (PCM)
        this.decoderProcess.stdout?.on('data', (chunk) => {
            // Emit raw audio chunk
            this.emit('audio', chunk);
            
            // TODO: Activity detection could go here to reset the timeout
        });
    }
}
