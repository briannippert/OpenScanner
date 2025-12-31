import { EventEmitter } from 'events';
import { Channel, CHANNELS, ScannerState } from '../models';

export class MockRadio extends EventEmitter {
    private state: ScannerState = {
        status: 'IDLE',
        signalStrength: 0
    };
    private isRunning: boolean = false;
    private scanInterval: NodeJS.Timeout | null = null;
    private channelIndex: number = 0;

    constructor() {
        super();
    }

    public start() {
        if (this.isRunning) return;
        this.isRunning = true;
        this.startScanning();
    }

    public stop() {
        this.isRunning = false;
        if (this.scanInterval) {
            clearInterval(this.scanInterval);
            this.scanInterval = null;
        }
        this.updateState({ status: 'IDLE', currentFrequency: undefined, currentChannel: undefined, signalStrength: 0 });
    }

    public holdFrequency(frequency: number) {
        console.log(`[Mock] Holding on ${frequency}`);
        // Mock implementation could set state here
    }

    public resumeScan() {
        console.log('[Mock] Resuming scan');
    }

    public getState(): ScannerState {
        return this.state;
    }

    private updateState(newState: Partial<ScannerState>) {
        this.state = { ...this.state, ...newState };
        this.emit('state-change', this.state);
    }

    private startScanning() {
        if (!this.isRunning) return;

        // Rapidly switch channels to simulate scanning
        this.scanInterval = setInterval(() => {
            this.channelIndex = (this.channelIndex + 1) % CHANNELS.length;
            const channel = CHANNELS[this.channelIndex];

            this.updateState({
                status: 'SCANNING',
                currentFrequency: channel.frequency,
                currentChannel: channel,
                signalStrength: Math.floor(Math.random() * 20) // Low background noise
            });

            // Random chance to "lock on" to a signal
            if (Math.random() < 0.1) { // 10% chance per tick
                this.lockOn(channel);
            }

        }, 200); // Switch every 200ms
    }

    private lockOn(channel: Channel) {
        if (this.scanInterval) {
            clearInterval(this.scanInterval);
            this.scanInterval = null;
        }

        // Simulate receiving transmission
        this.updateState({
            status: 'RECEIVING',
            currentFrequency: channel.frequency,
            currentChannel: channel,
            signalStrength: 80 + Math.floor(Math.random() * 20) // Strong signal
        });

        // Hold for random duration (3-10 seconds)
        const duration = 3000 + Math.random() * 7000;
        
        setTimeout(() => {
            if (this.isRunning) {
                this.startScanning();
            }
        }, duration);
    }
}
