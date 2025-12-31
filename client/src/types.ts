export interface Channel {
    frequency: number;
    license: string;
    type: string;
    tone: string;
    alphaTag: string;
    description: string;
    mode: string;
    tag: string;
}

export interface ScannerState {
    status: 'SCANNING' | 'RECEIVING' | 'IDLE';
    currentFrequency?: number;
    currentChannel?: Channel;
    signalStrength: number; // 0-100
    isAudioStreaming?: boolean;
}

declare global {
    interface Window {
        audioCtx?: AudioContext;
        nextStartTime?: number;
    }
}
