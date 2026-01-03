import React, { useEffect, useState } from 'react';
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
    onScan?: () => void;
    channels?: Channel[];
}

const ScannerDisplay: React.FC<Props> = ({ state, analyser, onScan, channels = [] }) => {
    const isReceiving = state.status === 'RECEIVING';
    const activeColor = isReceiving ? '#00ff00' : '#555';
    
    // Virtual display state for scanning animation
    const [scanIndex, setScanIndex] = useState(0);

    useEffect(() => {
        if (state.status === 'SCANNING' && channels.length > 0) {
            const interval = setInterval(() => {
                setScanIndex(prev => (prev + 1) % channels.length);
            }, 500); // Half-second cycle
            return () => clearInterval(interval);
        }
    }, [state.status, channels]);

    // Determine what to show
    let displayFreq = state.currentFrequency;
    let displayAlpha = state.currentChannel?.alphaTag;

    if (state.status === 'SCANNING' && channels.length > 0) {
        // Show cycling channels
        displayFreq = channels[scanIndex].frequency;
        displayAlpha = channels[scanIndex].alphaTag;
    }

    return (
        <Paper 
            elevation={6} 
            sx={{ 
                p: 2, 
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
                <Box display="flex" justifyContent="space-between" alignItems="center" mb={2}>
                    <Box display="flex" alignItems="center" gap={1}>
                        <RadioIcon sx={{ color: activeColor }} />
                        <Typography variant="overline" sx={{ color: activeColor, fontWeight: 'bold', letterSpacing: 2 }}>
                            {state.status}
                        </Typography>
                    </Box>
                    <Box display="flex" gap={1} alignItems="center">
                        {onScan && state.status !== 'SCANNING' && state.status !== 'IDLE' && (
                            <Chip 
                                label={state.status === 'RECEIVING' || state.status === 'MONITORING' ? "SKIP" : "RESUME SCAN"} 
                                color="primary" 
                                variant="outlined" 
                                size="small" 
                                onClick={onScan}
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
                        </Box>
                        {isReceiving && state.sourceID && (
                            <Typography variant="caption" sx={{ 
                                color: state.sourceID < 100 ? '#00ffff' : '#ffaa00', 
                                fontWeight: 'bold',
                                mt: 0.5,
                                display: 'block',
                                letterSpacing: 1
                            }}>
                                {state.sourceID < 100 ? `[BASE]` : `[UNIT ${state.sourceID}]`}
                            </Typography>
                        )}
                    </Box>
                    
                    {/* VU Meter (Only when receiving) */}
                    {isReceiving && analyser && (
                        <Box>
                             <VuMeter analyser={analyser} height={60} width={10} />
                        </Box>
                    )}
                </Box>

                {/* Visualizers */}
                <Box sx={{ mt: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 1 }}>
                    {/* Audio Waterfall (Heatmap during receive or monitoring) */}
                    {(state.status === 'RECEIVING' || state.status === 'MONITORING') && analyser && (
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
