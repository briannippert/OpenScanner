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
    /**
     * Measured frequency error of the received carrier, in Hz, or absent when idle. Over a
     * transmitter known to be on-frequency this is the dongle's crystal error.
     */
    measuredOffsetHz?: number;
}

export interface ScannerState {
    status: 'SCANNING' | 'RECEIVING' | 'MONITORING' | 'IDLE' | 'DEBUG';
    isHardwareConnected?: boolean;
    deviceName?: string;
    devicePort?: string;
    currentFrequency?: number;
    currentChannel?: Channel;
    signalStrength: number; // 0-100
    currentSignalDb?: number;
    isAudioStreaming?: boolean;
    squelch?: number;
    gain?: number;
    ppm?: number;
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

export interface RadioEvent {
    id: string;
    timestamp: string;
    type: 'TONE_OUT';
    label: string;
    frequency: number;
    alphaTag?: string;
    toneA?: number;
    toneB?: number;
    transmissionId?: string;
}

/** A per-channel display name for a source ID (SRC) or talkgroup (TG). */
export interface RadioAlias {
    id?: number;
    kind: 'SRC' | 'TG';
    value: number;
    name: string;
    alphaTag: string;
    frequency: number;
}

/** A distinct SRC/TG seen on a channel within the lookback window. */
export interface AliasCandidate {
    alphaTag: string;
    frequency: number;
    kind: 'SRC' | 'TG';
    value: number;
    count: number;
    lastSeen?: string;
}

export type UpdateState = 'idle' | 'checking' | 'available' | 'updating' | 'success' | 'failed';

export interface UpdateStatus {
    state: UpdateState;
    currentVersion: string;
    currentCommit: string;
    latestTag?: string;
    latestName?: string;
    releaseNotes?: string;
    releaseUrl?: string;
    commitsBehind: number;
    updateAvailable: boolean;
    phase?: string;
    log: string[];
    error?: string;
    lastCheckedUtc?: string;
}

/** A single live self-update progress message from the control WebSocket. */
export interface UpdateProgress {
    phase: string;
    line: string;
    state: UpdateState;
}

declare global {
    interface Window {
        audioCtx?: AudioContext;
        webkitAudioContext?: typeof AudioContext;
        nextStartTime?: number;
    }
}
