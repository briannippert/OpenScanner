import React from 'react';
import { Box, Typography, Paper, Chip, ThemeProvider } from '@mui/material';
import { alpha } from '@mui/material/styles';
import type { ScannerState, Channel } from '../types';
import RadioIcon from '@mui/icons-material/Radio';
import SignalCellularAltIcon from '@mui/icons-material/SignalCellularAlt';
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import AudioSpectrogram from './AudioSpectrogram';
import VuMeter from './VuMeter';
import { accent, surface, status, text } from '../theme/tokens';
import { readoutTheme } from '../theme/theme';

interface Props {
    state: ScannerState;
    analyser?: AnalyserNode;
    onScan?: (freq?: number) => void;
    channels?: Channel[];
}

const monoFont = { fontFamily: '"Roboto Mono", ui-monospace, monospace' };

const ScannerDisplay: React.FC<Props> = ({ state, analyser, onScan, channels = [] }) => {
    const isReceiving = state.status === 'RECEIVING';
    const isParallel = !!state.parallelChannels && state.parallelChannels.length > 0;
    const isFastScan = !isParallel && state.status === 'SCANNING' && !state.currentFrequency && channels.length > 1;
    const hasParallelActivity = isParallel && state.parallelChannels!.some(pc => pc.isActive);
    const isActive = isReceiving || hasParallelActivity;
    const activeColor = isActive ? accent.main : surface.borderStrong;

    const displayFreq = state.currentFrequency;
    const displayAlpha = state.currentChannel?.alphaTag;
    const displayMode = state.currentChannel?.mode;
    const fastScanChannels = isFastScan ? channels.filter(c => !c.avoid) : [];

    const getModeLabel = (mode: string): string => {
        const upper = mode.toUpperCase();
        if (upper === 'NFM' || upper === 'FM') return 'ANALOG';
        return upper; // P25, AM, WFM, etc.
    };
    const getModeBgColor = (mode: string): string =>
        mode.toUpperCase() === 'P25' ? alpha(status.info, 0.2) : alpha('#ffffff', 0.07);
    const getModeTextColor = (mode: string): string =>
        mode.toUpperCase() === 'P25' ? status.info : text.disabled;

    const infoChipSx = {
        height: 20, fontSize: '0.7rem', ...monoFont,
        bgcolor: alpha('#ffffff', 0.08), color: text.secondary,
    };

    return (
      <ThemeProvider theme={readoutTheme}>
        <Paper
            elevation={0}
            sx={{
                p: { xs: 1, sm: 2 },
                bgcolor: 'surface.base',
                color: 'text.primary',
                borderRadius: 2,
                border: '1px solid',
                borderColor: isReceiving ? 'primary.main' : 'surface.border',
                boxShadow: isReceiving ? `0 0 24px ${accent.glow}` : 'none',
                position: 'relative',
                overflow: 'hidden',
                transition: 'border-color 0.3s ease, box-shadow 0.3s ease',
            }}
        >
            {/* Background tech texture */}
            <Box sx={{
                position: 'absolute', inset: 0, opacity: 0.05, zIndex: 0,
                background: `repeating-linear-gradient(45deg, ${surface.base}, ${surface.base} 10px, ${surface.raised} 10px, ${surface.raised} 20px)`,
            }} />

            <Box position="relative" zIndex={1}>
                {/* Header status line */}
                <Box display="flex" justifyContent="space-between" alignItems="center" mb={2} sx={{ flexWrap: 'wrap', gap: 1 }}>
                    <Box display="flex" alignItems="center" gap={1}>
                        <RadioIcon sx={{ color: activeColor, ...(isActive ? { filter: `drop-shadow(0 0 6px ${accent.glow})` } : {}) }} />
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
                                borderColor: state.signalStrength > 20 ? activeColor : surface.border,
                                color: state.signalStrength > 20 ? activeColor : text.disabled,
                                '& .MuiChip-icon': { color: 'inherit' },
                            }}
                        />
                    </Box>
                </Box>

                {/* Main frequency display */}
                <Box py={1} sx={{
                    borderTop: '1px solid', borderBottom: '1px solid', borderColor: 'surface.border',
                    mb: 1, bgcolor: 'surface.surface',
                    display: 'flex', flexDirection: 'row', alignItems: 'center', justifyContent: 'center', gap: 2,
                }}>
                    <Box textAlign="center">
                        {isParallel ? (
                            <>
                                <Typography variant="h3" sx={{
                                    ...monoFont, fontWeight: 700,
                                    color: hasParallelActivity ? accent.main : surface.borderStrong,
                                    letterSpacing: 3, fontSize: { xs: '1.8rem', md: '2.5rem' }, mb: 1,
                                    textShadow: hasParallelActivity ? `0 0 10px ${accent.glow}` : 'none',
                                }}>
                                    PARALLEL SCAN
                                </Typography>
                                <Box display="flex" flexDirection="column" gap={1} width="100%">
                                    {state.parallelChannels!.map(pc => {
                                        const dbMin = -60;
                                        const dbMax = -10;
                                        const clampedDb = Math.max(dbMin, Math.min(dbMax, pc.signalStrength));
                                        const meterPct = ((clampedDb - dbMin) / (dbMax - dbMin)) * 100;
                                        const meterColor = pc.isActive ? accent.main : (meterPct > 30 ? status.warn : surface.border);
                                        return (
                                            <Box key={pc.channel.frequency} display="flex" alignItems="center" gap={1}>
                                                <Typography sx={{
                                                    ...monoFont, fontSize: '0.7rem',
                                                    color: pc.isActive ? accent.main : text.secondary,
                                                    fontWeight: pc.isActive ? 'bold' : 'normal',
                                                    minWidth: { xs: 60, sm: 90 },
                                                    whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
                                                    transition: 'color 0.3s ease',
                                                }}>
                                                    {pc.channel.alphaTag || pc.channel.frequency}
                                                    {pc.isRecording ? ' [REC]' : ''}
                                                </Typography>
                                                <Box sx={{
                                                    flex: 1, height: 6, bgcolor: 'surface.raised', borderRadius: 1,
                                                    overflow: 'hidden', border: '1px solid', borderColor: 'surface.border',
                                                }}>
                                                    <Box sx={{
                                                        width: `${meterPct}%`, height: '100%', bgcolor: meterColor, borderRadius: 1,
                                                        transition: 'width 0.15s linear, background-color 0.3s ease',
                                                        boxShadow: pc.isActive ? `0 0 6px ${meterColor}` : 'none',
                                                    }} />
                                                </Box>
                                                <Typography sx={{ ...monoFont, fontSize: '0.6rem', color: text.disabled, minWidth: 36, textAlign: 'right' }}>
                                                    {pc.signalStrength > -90 ? `${pc.signalStrength.toFixed(0)}dB` : '---'}
                                                </Typography>
                                            </Box>
                                        );
                                    })}
                                </Box>
                                {state.parallelChannels!.filter(pc => pc.isActive && pc.sourceID).map(pc => (
                                    <Typography key={pc.channel.frequency} variant="caption" sx={{
                                        color: status.warn, fontWeight: 'bold', mt: 0.5, display: 'block', ...monoFont, fontSize: '0.65rem',
                                    }}>
                                        {pc.channel.alphaTag}: {pc.sourceID ?? '?'} {pc.targetID ? `-> TG ${pc.targetID}` : ''}
                                    </Typography>
                                ))}
                            </>
                        ) : isFastScan ? (
                            <>
                                <Typography variant="h3" sx={{
                                    ...monoFont, fontWeight: 700, color: surface.borderStrong,
                                    letterSpacing: 3, fontSize: { xs: '2rem', md: '3rem' }, mb: 1,
                                }}>
                                    FAST SCAN
                                </Typography>
                                <Box display="flex" justifyContent="center" flexWrap="wrap" gap={0.5}>
                                    {fastScanChannels.map(ch => (
                                        <Chip
                                            key={ch.frequency}
                                            label={ch.alphaTag || ch.frequency.toString()}
                                            size="small"
                                            sx={{ height: 20, fontSize: '0.65rem', bgcolor: 'surface.raised', color: text.disabled, ...monoFont }}
                                        />
                                    ))}
                                </Box>
                            </>
                        ) : (
                            <>
                                <Typography variant="h3" sx={{
                                    ...monoFont, fontWeight: 700, color: activeColor,
                                    textShadow: isReceiving ? `0 0 10px ${accent.glow}` : 'none',
                                    fontSize: { xs: '2rem', md: '3rem' },
                                }}>
                                    {displayFreq ? displayFreq.toFixed(4) : '---.----'}
                                </Typography>
                                <Box display="flex" justifyContent="center" gap={1} alignItems="center">
                                    <Typography variant="caption" color="text.secondary" sx={{ letterSpacing: 2 }}>MHz</Typography>
                                    {displayAlpha && <Chip label={displayAlpha} size="small" sx={infoChipSx} />}
                                    {displayMode && (
                                        <Chip
                                            label={getModeLabel(displayMode)}
                                            size="small"
                                            sx={{ ...infoChipSx, bgcolor: getModeBgColor(displayMode), color: getModeTextColor(displayMode), fontWeight: 'bold', letterSpacing: 1 }}
                                        />
                                    )}
                                    {state.currentTone && state.currentTone !== 'ANALOG' && state.currentTone !== 'EMRG' && (
                                        <Chip label={state.currentTone} size="small" sx={{ ...infoChipSx, color: status.info }} />
                                    )}
                                    {state.currentTone === 'EMRG' && (
                                        <Chip
                                            label="! EMERGENCY"
                                            size="small"
                                            sx={{
                                                height: 22, fontSize: '0.75rem', fontWeight: 'bold', ...monoFont, letterSpacing: 1,
                                                bgcolor: status.error, color: '#fff',
                                                animation: 'pulse 0.6s infinite alternate', border: `1px solid ${alpha(status.error, 0.6)}`,
                                            }}
                                        />
                                    )}
                                    {state.lastDetectedTone && (
                                        <Chip
                                            label={`ALERT: ${state.lastDetectedTone}`}
                                            size="small"
                                            color="error"
                                            variant="filled"
                                            sx={{ height: 20, fontSize: '0.7rem', fontWeight: 'bold', ...monoFont, animation: 'pulse 1s infinite' }}
                                        />
                                    )}
                                </Box>
                                {isReceiving && (state.speakerChain || state.sourceID || state.targetID) && (
                                    <Typography variant="caption" sx={{ color: status.warn, fontWeight: 'bold', mt: 0.5, display: 'block', ...monoFont, letterSpacing: 1 }}>
                                        {state.speakerChain
                                            ? `${state.speakerChain} → TG ${state.targetID ?? '?'}`
                                            : `${state.sourceID ?? '?'} → TG ${state.targetID ?? '?'}`}
                                    </Typography>
                                )}
                            </>
                        )}
                    </Box>

                    {/* VU meter (when receiving or parallel activity) */}
                    {isActive && analyser && (
                        <Box><VuMeter analyser={analyser} height={60} width={10} /></Box>
                    )}
                </Box>

                {/* Visualizers */}
                <Box sx={{ mt: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 1 }}>
                    {(state.status === 'RECEIVING' || state.status === 'MONITORING' || hasParallelActivity) && analyser && (
                        <AudioSpectrogram analyser={analyser} height={60} />
                    )}

                    {state.lastTranscription && (
                        <Box sx={{
                            width: '100%', p: 1.5, bgcolor: alpha('#000000', 0.6), borderRadius: 1.5,
                            border: '1px solid', borderColor: 'surface.border', mt: 1, minHeight: 40,
                            display: 'flex', alignItems: 'center', justifyContent: 'center', textAlign: 'center',
                        }}>
                            <Typography variant="body2" sx={{ color: accent.main, fontStyle: 'italic', ...monoFont, fontSize: '0.9rem', textShadow: `0 0 5px ${accent.glow}` }}>
                                "{state.lastTranscription}"
                            </Typography>
                        </Box>
                    )}
                </Box>

            </Box>
        </Paper>
      </ThemeProvider>
    );
};

export default ScannerDisplay;
