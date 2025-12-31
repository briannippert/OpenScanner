import React from 'react';
import { Box, Typography, Paper, LinearProgress, Chip } from '@mui/material';
import type { ScannerState } from '../types';
import RadioIcon from '@mui/icons-material/Radio';
import SignalCellularAltIcon from '@mui/icons-material/SignalCellularAlt';
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import AudioSpectrogram from './AudioSpectrogram';
import VuMeter from './VuMeter';

interface Props {
    state: ScannerState;
    analyser?: AnalyserNode;
    onScan?: () => void;
}

const ScannerDisplay: React.FC<Props> = ({ state, analyser, onScan }) => {
    const isReceiving = state.status === 'RECEIVING';
    const activeColor = isReceiving ? '#00ff00' : '#555';

    return (
        <Paper 
            elevation={6} 
            sx={{ 
                p: 4, 
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
                                label="RESUME SCAN" 
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
                <Box py={4} sx={{ 
                    borderTop: '1px solid #222', 
                    borderBottom: '1px solid #222', 
                    my: 2, 
                    bgcolor: '#0f0f0f', 
                    display: 'flex', 
                    flexDirection: { xs: 'column', md: 'row' },
                    alignItems: 'center', 
                    justifyContent: 'center', 
                    gap: 4 
                }}>
                    <Box textAlign="center">
                        <Typography variant="h1" sx={{ 
                            fontFamily: 'monospace', 
                            fontWeight: 700, 
                            color: activeColor,
                            textShadow: isReceiving ? '0 0 10px rgba(0,255,0,0.5)' : 'none',
                            fontSize: { xs: '3rem', md: '5rem' }
                        }}>
                            {state.currentFrequency ? state.currentFrequency.toFixed(4) : '---.----'}
                        </Typography>
                        <Typography variant="overline" color="text.secondary" sx={{ letterSpacing: 4 }}>
                            MEGAHERTZ
                        </Typography>
                    </Box>
                    
                    {/* VU Meter (Only when receiving) */}
                    {isReceiving && analyser && (
                        <Box>
                             <VuMeter analyser={analyser} height={100} width={15} />
                        </Box>
                    )}
                </Box>

                {/* Visualizers */}
                <Box sx={{ mt: 3, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 1 }}>
                    {/* Audio Waterfall (Heatmap during receive or monitoring) */}
                    {(state.status === 'RECEIVING' || state.status === 'MONITORING') && analyser && (
                        <AudioSpectrogram analyser={analyser} height={200} />
                    )}
                </Box>
                {/* Signal Bar Bottom */}
                <Box mt={3}>
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
