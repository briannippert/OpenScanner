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
    avoid: boolean;
    dmrSlot?: number;
    dmrColorCode?: number;
    dmrTalkgroup?: number;
}

export interface ParallelChannelState {
    channel: Channel;
    isActive: boolean;
    signalStrength: number;
    isRecording: boolean;
    sourceID?: number;
    targetID?: number;
    speakerChain?: string;
    currentTone?: string;
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
    lastTranscription?: string;
    sourceID?: number;
    targetID?: number;
    speakerChain?: string;
    currentTone?: string;
    lastDetectedTone?: string;
    parallelChannels?: ParallelChannelState[];
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
    sourceID?: number;
    targetID?: number;
    speakerChain?: string;
    detectedTone?: string;
    isFavorite?: boolean;
}

export interface FireToneSet {
    id?: number;
    name: string;
    frequencyA: number;
    frequencyB: number;
    description?: string;
}

declare global {
    interface Window {
        audioCtx?: AudioContext;
        webkitAudioContext?: typeof AudioContext;
        nextStartTime?: number;
    }
}
