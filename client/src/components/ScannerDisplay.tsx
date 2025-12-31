import React, { useEffect } from 'react';
import { Box, Typography, Paper, LinearProgress, Chip } from '@mui/material';
import type { ScannerState } from '../types';
import RadioIcon from '@mui/icons-material/Radio';
import VolumeUpIcon from '@mui/icons-material/VolumeUp';

interface Props {
    state: ScannerState;
}

const ScannerDisplay: React.FC<Props> = ({ state }) => {
    // Audio simulation (white noise + garbled speech)
    useEffect(() => {
        if (state.status === 'RECEIVING') {
             // Placeholder for where actual WebAudio API logic would go to play the stream
             // For now, we just visually represent it.
        }
    }, [state.status]);

    return (
        <Paper 
            elevation={3} 
            sx={{ 
                p: 3, 
                mb: 2, 
                bgcolor: '#121212', 
                color: '#00ff00', 
                fontFamily: 'monospace',
                border: '1px solid #333'
            }}
        >
            <Box display="flex" alignItems="center" justifyContent="space-between" mb={2}>
                <Typography variant="h5" component="div" sx={{ fontWeight: 'bold' }}>
                    <RadioIcon sx={{ verticalAlign: 'middle', mr: 1 }} />
                    {state.status}
                </Typography>
                {state.status === 'RECEIVING' && <VolumeUpIcon color="primary" />}
            </Box>

            <Box sx={{ mb: 3, textAlign: 'center' }}>
                <Typography variant="h2" sx={{ fontWeight: 'bold', color: state.status === 'RECEIVING' ? '#00ff00' : '#555' }}>
                    {state.currentFrequency ? state.currentFrequency.toFixed(4) : '---.----'} <span style={{fontSize: '1rem'}}>MHz</span>
                </Typography>
            </Box>

            {state.currentChannel ? (
                <Box>
                    <Typography variant="h6">{state.currentChannel.alphaTag}</Typography>
                    <Typography variant="body1">{state.currentChannel.description}</Typography>
                    <Box mt={1}>
                        <Chip label={state.currentChannel.mode} size="small" sx={{ bgcolor: '#333', color: '#fff', mr: 1 }} />
                        <Chip label={state.currentChannel.tag} size="small" sx={{ bgcolor: '#333', color: '#fff' }} />
                    </Box>
                </Box>
            ) : (
                <Box height={80} display="flex" alignItems="center" justifyContent="center">
                    <Typography variant="body2" color="gray">Scanning...</Typography>
                </Box>
            )}

            <Box mt={3}>
                <Typography variant="caption" display="block" gutterBottom>
                    Signal Strength
                </Typography>
                <LinearProgress 
                    variant="determinate" 
                    value={state.signalStrength} 
                    sx={{ 
                        height: 10, 
                        borderRadius: 5,
                        '& .MuiLinearProgress-bar': {
                            backgroundColor: state.signalStrength > 50 ? '#00ff00' : '#aaaaaa'
                        }
                    }} 
                />
            </Box>
        </Paper>
    );
};

export default ScannerDisplay;
