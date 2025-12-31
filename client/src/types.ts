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
    status: 'SCANNING' | 'RECEIVING' | 'MONITORING' | 'IDLE';
    currentFrequency?: number;
    currentChannel?: Channel;
    signalStrength: number; // 0-100
    isAudioStreaming?: boolean;
    rfSpectrum?: { frequency: number, db: number }[];
    gps?: {
        lat: number;
        lon: number;
        alt: number;
        speed: number;
        time: string;
        fix: number;
        sats: number;
    };
}

export interface CallLog {
    id: string;
    timestamp: string;
    frequency: number;
    alphaTag: string;
    description: string;
    lat?: number;
    lon?: number;
    audio_path?: string;
    duration?: number;
}

declare global {
    interface Window {
        audioCtx?: AudioContext;
        nextStartTime?: number;
    }
}
