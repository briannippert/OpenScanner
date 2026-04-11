import React from 'react';
import { Box } from '@mui/material';

const SPEAKER_COLORS = [
    '#00ff00', // green
    '#00bfff', // deep sky blue
    '#ffa500', // orange
    '#ff69b4', // hot pink
    '#ffff00', // yellow
    '#00ffcc', // aquamarine
    '#da70d6', // orchid
    '#ff6347', // tomato
];

function getSpeakerColor(speakerNum: number): string {
    return SPEAKER_COLORS[(speakerNum - 1) % SPEAKER_COLORS.length];
}

interface SpeakerSegment {
    speaker: number;
    label: string;
    text: string;
}

function parseSegments(text: string): SpeakerSegment[] | null {
    const pattern = /\[Speaker (\d+)\]:\s*/g;
    const matches = [...text.matchAll(pattern)];
    if (matches.length === 0) return null;

    const segments: SpeakerSegment[] = [];
    for (let i = 0; i < matches.length; i++) {
        const match = matches[i];
        const speakerNum = parseInt(match[1], 10);
        const start = match.index! + match[0].length;
        const end = i + 1 < matches.length ? matches[i + 1].index! : text.length;
        const segText = text.slice(start, end).trim();
        if (segText) {
            segments.push({ speaker: speakerNum, label: `Speaker ${speakerNum}`, text: segText });
        }
    }
    return segments.length > 0 ? segments : null;
}

interface SpeakerTextProps {
    text: string;
    baseColor?: string;
    fontSize?: string;
    fontStyle?: string;
    fontFamily?: string;
    textShadow?: string;
    showQuotes?: boolean;
}

const SpeakerText: React.FC<SpeakerTextProps> = ({
    text,
    baseColor = '#00ff00',
    fontSize = '0.9rem',
    fontStyle = 'italic',
    fontFamily,
    textShadow,
    showQuotes = true,
}) => {
    const segments = parseSegments(text);

    if (!segments) {
        return (
            <span style={{ color: baseColor, fontStyle, fontFamily, fontSize, textShadow }}>
                {showQuotes ? `"${text}"` : text}
            </span>
        );
    }

    return (
        <Box component="span" sx={{ fontStyle, fontFamily, fontSize, textShadow }}>
            {showQuotes && '"'}
            {segments.map((seg, i) => (
                <React.Fragment key={i}>
                    <span style={{ color: getSpeakerColor(seg.speaker), fontWeight: 'bold' }}>
                        [{seg.label}]:
                    </span>{' '}
                    <span style={{ color: baseColor }}>
                        {seg.text}
                    </span>
                    {i < segments.length - 1 && ' '}
                </React.Fragment>
            ))}
            {showQuotes && '"'}
        </Box>
    );
};

export default SpeakerText;
