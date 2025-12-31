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

export const CHANNELS: Channel[] = [
    {
        frequency: 155.0325,
        license: "WQGI420",
        type: "RM",
        tone: "117 NAC",
        alphaTag: "Salem Police",
        description: "Police Operations",
        mode: "P25",
        tag: "Law Dispatch"
    },
    {
        frequency: 155.8875,
        license: "WPMN513",
        type: "RM",
        tone: "117 NAC",
        alphaTag: "Salem Fire",
        description: "Fire Operations",
        mode: "P25",
        tag: "Fire Dispatch"
    }
];

export interface ScannerState {
    status: 'SCANNING' | 'RECEIVING' | 'IDLE';
    currentFrequency?: number;
    currentChannel?: Channel;
    signalStrength: number; // 0-100
    isAudioStreaming?: boolean;
}
