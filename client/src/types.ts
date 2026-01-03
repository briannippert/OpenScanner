export interface Channel {
    id?: number;
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
    isHardwareConnected?: boolean;
    deviceName?: string;
    devicePort?: string;
    currentFrequency?: number;
    currentChannel?: Channel;
    signalStrength: number; // 0-100
    currentSignalDb?: number;
    isAudioStreaming?: boolean;
    squelch?: number;
    rfSpectrum?: { frequency: number, db: number }[];
    gps?: {
        lat: number;
        lon: number;
        alt: number;
        speed: number;
        time: string;
        fix: number;
        sats: number;
        satsVisible?: number;
    };
    manualHoldFrequency?: number;
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
    transcription?: string;
}

declare global {
    interface Window {
        audioCtx?: AudioContext;
        nextStartTime?: number;
    }
}
