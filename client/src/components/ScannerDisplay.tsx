import React from 'react';
import { Box, Typography, Paper, LinearProgress, Chip, Grid } from '@mui/material';
import type { ScannerState } from '../types';
import RadioIcon from '@mui/icons-material/Radio';
import SignalCellularAltIcon from '@mui/icons-material/SignalCellularAlt';
import GraphicEqIcon from '@mui/icons-material/GraphicEq';

interface Props {
    state: ScannerState;
}

const ScannerDisplay: React.FC<Props> = ({ state }) => {
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
                    <Box display="flex" gap={1}>
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
                <Box textAlign="center" py={4} sx={{ borderTop: '1px solid #222', borderBottom: '1px solid #222', my: 2, bgcolor: '#0f0f0f' }}>
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

                {/* Channel Info */}
                <Grid container spacing={2} sx={{ mt: 2 }}>
                    <Grid item xs={12} md={6}>
                        <Box sx={{ p: 2, bgcolor: '#111', borderRadius: 1 }}>
                            <Typography variant="caption" color="gray" display="block">CHANNEL TAG</Typography>
                            <Typography variant="h6" sx={{ color: '#fff', fontWeight: 'bold' }}>
                                {state.currentChannel?.alphaTag || 'SCANNING...'}
                            </Typography>
                        </Box>
                    </Grid>
                    <Grid item xs={12} md={6}>
                        <Box sx={{ p: 2, bgcolor: '#111', borderRadius: 1 }}>
                            <Typography variant="caption" color="gray" display="block">DESCRIPTION</Typography>
                            <Typography variant="body1" sx={{ color: '#aaa' }}>
                                {state.currentChannel?.description || 'System Idle'}
                            </Typography>
                        </Box>
                    </Grid>
                </Grid>

                {/* Audio Visualizer Placeholder */}
                {isReceiving && (
                    <Box sx={{ mt: 3, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 1, color: '#00ff00' }}>
                        <GraphicEqIcon className="pulse-animation" />
                        <Typography variant="caption">AUDIO STREAM ACTIVE</Typography>
                    </Box>
                )}
                
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
