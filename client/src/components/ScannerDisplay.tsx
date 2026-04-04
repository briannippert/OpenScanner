import React from 'react';
import { Box, Typography, Paper, LinearProgress, Chip } from '@mui/material';
import type { ScannerState, Channel } from '../types';
import RadioIcon from '@mui/icons-material/Radio';
import SignalCellularAltIcon from '@mui/icons-material/SignalCellularAlt';
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import AudioSpectrogram from './AudioSpectrogram';
import VuMeter from './VuMeter';

interface Props {
    state: ScannerState;
    analyser?: AnalyserNode;
    onScan?: (freq?: number) => void;
    channels?: Channel[];
}

const ScannerDisplay: React.FC<Props> = ({ state, analyser, onScan, channels = [] }) => {
    const isReceiving = state.status === 'RECEIVING';
    const isParallel = !!state.parallelChannels && state.parallelChannels.length > 0;
    const isFastScan = !isParallel && state.status === 'SCANNING' && !state.currentFrequency && channels.length > 1;
    const hasParallelActivity = isParallel && state.parallelChannels!.some(pc => pc.isActive);
    const activeColor = isReceiving || hasParallelActivity ? '#00ff00' : '#555';

    const displayFreq = state.currentFrequency;
    const displayAlpha = state.currentChannel?.alphaTag;
    const displayMode = state.currentChannel?.mode;
    const fastScanChannels = isFastScan ? channels.filter(c => !c.avoid) : [];

    const getModeLabel = (mode: string): string => {
        const upper = mode.toUpperCase();
        if (upper === 'NFM' || upper === 'FM') return 'ANALOG';
        return upper; // P25, AM, WFM, etc.
    };

    const getModeBgColor = (mode: string): string => {
        const upper = mode.toUpperCase();
        if (upper === 'P25') return 'rgba(0, 150, 255, 0.2)';
        return 'rgba(255,255,255,0.07)';
    };

    const getModeTextColor = (mode: string): string => {
        const upper = mode.toUpperCase();
        if (upper === 'P25') return '#4da6ff';
        return '#666';
    };
    return (
        <Paper 
            elevation={6} 
            sx={{ 
                p: { xs: 1, sm: 2 }, 
                bgcolor: '#0a0a0a', 
                color: '#fff', 
                borderRadius: 2,
                border: `1px solid ${isReceiving ? '#00ff00' : '#333'}`,
                boxShadow: isReceiving ? '0 0 20px rgba(0, 255, 0, 0.2)' : 'none',
                position: 'relative',
                overflow: 'hidden',
                transition: 'all 0.3s ease'
            }}
        >
            {/* Background Tech Elements */}
            <Box sx={{
                position: 'absolute',
                top: 0, left: 0, right: 0, bottom: 0,
                opacity: 0.05,
                background: 'repeating-linear-gradient(45deg, #000, #000 10px, #111 10px, #111 20px)',
                zIndex: 0
            }} />

            <Box position="relative" zIndex={1}>
                {/* Header Status Line */}
                <Box display="flex" justifyContent="space-between" alignItems="center" mb={2} sx={{ flexWrap: 'wrap', gap: 1 }}>
                    <Box display="flex" alignItems="center" gap={1}>
                        <RadioIcon sx={{ color: activeColor }} />
                        <Typography variant="overline" sx={{ color: activeColor, fontWeight: 'bold', letterSpacing: 2 }}>
                            {state.status}
                        </Typography>
                    </Box>
                    <Box display="flex" gap={1} alignItems="center">
                        {onScan && state.status !== 'SCANNING' && state.status !== 'IDLE' && (
                            <Chip 
                                label="SKIP" 
                                color="primary" 
                                variant="outlined" 
                                size="small" 
                                onClick={() => onScan(state.currentFrequency || undefined)}
                                icon={<PlayArrowIcon />}
                                sx={{ cursor: 'pointer', height: 24, fontSize: '0.65rem' }}
                            />
                        )}
                        <Chip 
                            icon={<SignalCellularAltIcon />} 
                            label={`${state.signalStrength.toFixed(0)}%`} 
                            size="small" 
                            variant="outlined"
                            sx={{ 
                                borderColor: state.signalStrength > 20 ? activeColor : '#333', 
                                color: state.signalStrength > 20 ? activeColor : '#555',
                                '& .MuiChip-icon': { color: 'inherit' }
                            }} 
                        />
                    </Box>
                </Box>

                {/* Main Frequency Display */}
                <Box py={1} sx={{ 
                    borderTop: '1px solid #222', 
                    borderBottom: '1px solid #222', 
                    mb: 1, 
                    bgcolor: '#0f0f0f', 
                    display: 'flex', 
                    flexDirection: 'row',
                    alignItems: 'center', 
                    justifyContent: 'center', 
                    gap: 2 
                }}>
                    <Box textAlign="center">
                        {isParallel ? (
                            <>
                                <Typography variant="h3" sx={{
                                    fontFamily: 'monospace',
                                    fontWeight: 700,
                                    color: hasParallelActivity ? '#00ff00' : '#555',
                                    letterSpacing: 3,
                                    fontSize: { xs: '1.8rem', md: '2.5rem' },
                                    mb: 1,
                                    textShadow: hasParallelActivity ? '0 0 10px rgba(0,255,0,0.4)' : 'none'
                                }}>
                                    PARALLEL SCAN
                                </Typography>
                                <Box display="flex" justifyContent="center" flexWrap="wrap" gap={0.5}>
                                    {state.parallelChannels!.map(pc => (
                                        <Chip
                                            key={pc.channel.frequency}
                                            label={
                                                pc.isActive
                                                    ? `${pc.channel.alphaTag || pc.channel.frequency}${pc.isRecording ? ' [REC]' : ''}`
                                                    : pc.channel.alphaTag || pc.channel.frequency.toString()
                                            }
                                            size="small"
                                            onClick={onScan ? () => onScan(pc.channel.frequency) : undefined}
                                            sx={{
                                                height: 22,
                                                fontSize: '0.65rem',
                                                fontFamily: 'monospace',
                                                cursor: onScan ? 'pointer' : 'default',
                                                bgcolor: pc.isActive ? 'rgba(0, 255, 0, 0.15)' : '#1a1a1a',
                                                color: pc.isActive ? '#00ff00' : '#666',
                                                border: pc.isActive ? '1px solid #00ff00' : '1px solid transparent',
                                                boxShadow: pc.isActive ? '0 0 8px rgba(0, 255, 0, 0.3)' : 'none',
                                                transition: 'all 0.3s ease',
                                                fontWeight: pc.isActive ? 'bold' : 'normal',
                                            }}
                                        />
                                    ))}
                                </Box>
                                {state.parallelChannels!.filter(pc => pc.isActive && pc.sourceID).map(pc => (
                                    <Typography key={pc.channel.frequency} variant="caption" sx={{
                                        color: '#ffaa00',
                                        fontWeight: 'bold',
                                        mt: 0.5,
                                        display: 'block',
                                        fontFamily: 'monospace',
                                        fontSize: '0.65rem'
                                    }}>
                                        {pc.channel.alphaTag}: {pc.sourceID ?? '?'} {pc.targetID ? `-> TG ${pc.targetID}` : ''}
                                    </Typography>
                                ))}
                            </>
                        ) : isFastScan ? (
                            <>
                                <Typography variant="h3" sx={{
                                    fontFamily: 'monospace',
                                    fontWeight: 700,
                                    color: '#555',
                                    letterSpacing: 3,
                                    fontSize: { xs: '2rem', md: '3rem' },
                                    mb: 1
                                }}>
                                    FAST SCAN
                                </Typography>
                                <Box display="flex" justifyContent="center" flexWrap="wrap" gap={0.5}>
                                    {fastScanChannels.map(ch => (
                                        <Chip
                                            key={ch.frequency}
                                            label={ch.alphaTag || ch.frequency.toString()}
                                            size="small"
                                            sx={{ height: 20, fontSize: '0.65rem', bgcolor: '#1a1a1a', color: '#666', fontFamily: 'monospace' }}
                                        />
                                    ))}
                                </Box>
                            </>
                        ) : (
                            <>
                                <Typography variant="h3" sx={{ 
                                    fontFamily: 'monospace', 
                                    fontWeight: 700, 
                                    color: activeColor,
                                    textShadow: isReceiving ? '0 0 10px rgba(0,255,0,0.5)' : 'none',
                                    fontSize: { xs: '2rem', md: '3rem' }
                                }}>
                                    {displayFreq ? displayFreq.toFixed(4) : '---.----'}
                                </Typography>
                                <Box display="flex" justifyContent="center" gap={1} alignItems="center">
                                    <Typography variant="caption" color="text.secondary" sx={{ letterSpacing: 2 }}>
                                        MHz
                                    </Typography>
                            {displayAlpha && (
                                <Chip 
                                    label={displayAlpha} 
                                    size="small" 
                                    sx={{ 
                                        height: 20,
                                        fontSize: '0.7rem',
                                        bgcolor: 'rgba(255,255,255,0.1)', 
                                        color: '#aaa', 
                                        fontFamily: 'monospace' 
                                    }} 
                                />
                            )}
                            {displayMode && (
                                <Chip
                                    label={getModeLabel(displayMode)}
                                    size="small"
                                    sx={{
                                        height: 20,
                                        fontSize: '0.7rem',
                                        bgcolor: getModeBgColor(displayMode),
                                        color: getModeTextColor(displayMode),
                                        fontFamily: 'monospace',
                                        fontWeight: 'bold',
                                        letterSpacing: 1
                                    }}
                                />
                            )}
                            {state.currentTone && state.currentTone !== 'ANALOG' && state.currentTone !== 'EMRG' && (
                                <Chip 
                                    label={state.currentTone} 
                                    size="small" 
                                    sx={{ 
                                        height: 20,
                                        fontSize: '0.7rem',
                                        bgcolor: 'rgba(255,255,255,0.1)', 
                                        color: '#00ffff', 
                                        fontFamily: 'monospace' 
                                    }} 
                                />
                            )}
                            {state.currentTone === 'EMRG' && (
                                <Chip
                                    label="! EMERGENCY"
                                    size="small"
                                    sx={{
                                        height: 22,
                                        fontSize: '0.75rem',
                                        fontWeight: 'bold',
                                        fontFamily: 'monospace',
                                        letterSpacing: 1,
                                        bgcolor: '#ff0000',
                                        color: '#ffffff',
                                        animation: 'pulse 0.6s infinite alternate',
                                        border: '1px solid #ff6666',
                                    }}
                                />
                            )}
                            {state.lastDetectedTone && (
                                <Chip 
                                    label={`ALERT: ${state.lastDetectedTone}`} 
                                    size="small" 
                                    color="error"
                                    variant="filled"
                                    sx={{ 
                                        height: 20,
                                        fontSize: '0.7rem',
                                        fontWeight: 'bold',
                                        fontFamily: 'monospace',
                                        animation: 'pulse 1s infinite'
                                    }} 
                                />
                            )}
                        </Box>
                        {isReceiving && (state.speakerChain || state.sourceID || state.targetID) && (
                            <Typography variant="caption" sx={{ 
                                color: '#ffaa00', 
                                fontWeight: 'bold',
                                mt: 0.5,
                                display: 'block',
                                fontFamily: 'monospace',
                                letterSpacing: 1
                            }}>
                                {state.speakerChain
                                    ? `${state.speakerChain} → TG ${state.targetID ?? '?'}`
                                    : `${state.sourceID ?? '?'} → TG ${state.targetID ?? '?'}`}
                            </Typography>
                        )}
                            </>
                        )}
                    </Box>
                    
                    {/* VU Meter (When receiving or parallel activity) */}
                    {(isReceiving || hasParallelActivity) && analyser && (
                        <Box>
                             <VuMeter analyser={analyser} height={60} width={10} />
                        </Box>
                    )}
                </Box>

                {/* Visualizers */}
                <Box sx={{ mt: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 1 }}>
                    {/* Audio Waterfall (Heatmap during receive, monitoring, or active parallel) */}
                    {(state.status === 'RECEIVING' || state.status === 'MONITORING' || hasParallelActivity) && analyser && (
                        <AudioSpectrogram analyser={analyser} height={60} />
                    )}

                    {/* Transcription Overlay */}
                    {state.lastTranscription && (
                        <Box sx={{ 
                            width: '100%', 
                            p: 1.5, 
                            bgcolor: 'rgba(0,0,0,0.6)', 
                            borderRadius: 1, 
                            border: '1px solid #333',
                            mt: 1,
                            minHeight: 40,
                            display: 'flex',
                            alignItems: 'center',
                            justifyContent: 'center',
                            textAlign: 'center'
                        }}>
                            <Typography variant="body2" sx={{ 
                                color: '#00ff00', 
                                fontStyle: 'italic',
                                fontFamily: 'monospace',
                                fontSize: '0.9rem',
                                textShadow: '0 0 5px rgba(0,255,0,0.3)'
                            }}>
                                "{state.lastTranscription}"
                            </Typography>
                        </Box>
                    )}
                </Box>
                {/* Signal Bar Bottom */}
                <Box mt={1}>
                    <LinearProgress 
                        variant="determinate" 
                        value={state.signalStrength} 
                        sx={{ 
                            height: 4, 
                            bgcolor: '#222',
                            '& .MuiLinearProgress-bar': {
                                bgcolor: activeColor,
                                boxShadow: `0 0 8px ${activeColor}`
                            }
                        }} 
                    />
                </Box>
            </Box>
            
            <style>{`
                @keyframes pulse {
                    0% { opacity: 0.5; }
                    50% { opacity: 1; }
                    100% { opacity: 0.5; }
                }
                .pulse-animation {
                    animation: pulse 1s infinite;
                }
            `}</style>
        </Paper>
    );
};

export default ScannerDisplay;
