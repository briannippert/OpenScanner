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
    /** The SDR this channel is pinned to in Manual assignment mode; null/absent lets the planner place it. */
    sdrDeviceId?: string | null;
    /** City/state this channel serves; grounds AI address lookups for its calls. */
    city?: string | null;
    state?: string | null;
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
    /** The SDR carrying this channel. */
    deviceId?: string;
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
    /** Live state of each SDR. */
    devices?: SdrDeviceState[];
}

export type SdrRole = 'FastScan' | 'Hop' | 'Idle';
export type GainClass = 'Analog' | 'Digital' | 'Mixed';

/** Live state of one SDR, broadcast with the scanner state. */
export interface SdrDeviceState {
    id: string;
    label: string;
    product: string;
    serial: string;
    attached: boolean;
    enabled: boolean;
    role: SdrRole;
    status: ScannerState['status'];
    isHardwareConnected: boolean;
    gainClass: GainClass;
    gain?: number;
    centerFrequency?: number;
    currentFrequency?: number;
    frequencies: number[];
}

/** One dongle and its stored settings (GET /api/radios). */
export interface Radio {
    id: string;
    label: string;
    serial: string;
    product: string;
    attached: boolean;
    index?: number | null;
    serialIsUnique: boolean;
    enabled: boolean;
    ppm: number;
    gainAnalog: number;
    gainDigital: number;
    gainMixed: number;
    role: SdrRole;
}

/** What one SDR carries under a plan. */
export interface PlanDevice {
    deviceId: string;
    label: string;
    role: SdrRole;
    gainClass: GainClass;
    gain: number;
    centerFrequency?: number | null;
    spanMHz: number;
    frequencies: number[];
    channelIds: number[];
}

/** Which SDR carries each channel. */
export interface SdrPlan {
    totalChannels: number;
    fastScanned: number;
    scanned: number;
    devices: PlanDevice[];
    autoPlacedChannelIds: number[];
    unscannedChannelIds: number[];
}

export type AssignmentMode = 'auto' | 'manual';

export interface RadiosResponse {
    mode: AssignmentMode;
    devices: Radio[];
    plan: SdrPlan;
    warnings: string[];
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
    /** AI call analysis, when the remote server produced one (see /api/ai). */
    insight?: TransmissionInsight | null;
}

/** AI analysis of one transmission (remote WhisperServer /transmission). */
export interface TransmissionInsight {
    transmissionId: string;
    analyzedAt: string;
    /** False for routine chatter. */
    isIncident: boolean;
    callType?: string | null;
    service?: string | null;
    summary?: string | null;
    incidentStatus?: string | null;
    address?: string | null;
    placeName?: string | null;
    crossStreet?: string | null;
    city?: string | null;
    state?: string | null;
    matchedAddress?: string | null;
    lat?: number | null;
    lon?: number | null;
    /** Groups transmissions about the same incident; null for routine or unlocated calls. */
    incidentId?: string | null;
}

/** A geolocated incident for the map (GET /api/ai/calls). */
export interface MapCall {
    id: string;
    timestamp: string;
    frequency: number;
    alphaTag: string;
    audio_path?: string | null;
    duration?: number | null;
    transcription?: string | null;
    callType?: string | null;
    service?: string | null;
    summary?: string | null;
    incidentStatus?: string | null;
    address?: string | null;
    placeName?: string | null;
    matchedAddress?: string | null;
    lat: number;
    lon: number;
    /** Calls sharing this are the same incident (same service, same spot, close in time). */
    incidentId?: string | null;
}

/** GET /api/ai/capabilities */
export interface AiCapabilities {
    supported: boolean;
    enabled: boolean;
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
