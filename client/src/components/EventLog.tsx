import { useState } from 'react';
import { Box, Typography, IconButton, Collapse, List, ListItem, Tooltip } from '@mui/material';
import NotificationsActiveIcon from '@mui/icons-material/NotificationsActive';
import DeleteSweepIcon from '@mui/icons-material/DeleteSweep';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import ExpandLessIcon from '@mui/icons-material/ExpandLess';
import LaunchIcon from '@mui/icons-material/Launch';
import type { RadioEvent } from '../types';

interface EventLogProps {
    events: RadioEvent[];
    onClear: () => void;
    onEventClick?: (event: RadioEvent) => void;
}

const formatDetail = (e: RadioEvent): string => {
    if (e.toneA != null && e.toneB) return `${e.toneA.toFixed(0)} / ${e.toneB.toFixed(0)} Hz`;
    if (e.toneA != null) return `${e.toneA.toFixed(0)} Hz`;
    return '';
};

export default function EventLog({ events, onClear, onEventClick }: EventLogProps) {
    const [open, setOpen] = useState(true);

    return (
        <Box sx={{ borderBottom: '1px solid #222', flexShrink: 0 }}>
            <Box
                display="flex"
                alignItems="center"
                justifyContent="space-between"
                sx={{ px: 1.5, py: 1, cursor: 'pointer' }}
                onClick={() => setOpen(o => !o)}
            >
                <Box display="flex" alignItems="center" gap={1}>
                    <NotificationsActiveIcon sx={{ color: '#ffb74d', fontSize: 18 }} />
                    <Typography sx={{ color: '#eee', fontWeight: 'bold', fontSize: '0.8rem', letterSpacing: 0.5 }}>
                        FIRE TONE-OUTS
                    </Typography>
                    <Typography sx={{ color: '#888', fontSize: '0.75rem' }}>({events.length})</Typography>
                </Box>
                <Box display="flex" alignItems="center">
                    {events.length > 0 && (
                        <Tooltip title="Clear events">
                            <IconButton
                                size="small"
                                onClick={(ev) => { ev.stopPropagation(); onClear(); }}
                                sx={{ color: '#888' }}
                            >
                                <DeleteSweepIcon fontSize="small" />
                            </IconButton>
                        </Tooltip>
                    )}
                    <IconButton size="small" sx={{ color: '#888' }}>
                        {open ? <ExpandLessIcon fontSize="small" /> : <ExpandMoreIcon fontSize="small" />}
                    </IconButton>
                </Box>
            </Box>

            <Collapse in={open} timeout="auto" unmountOnExit>
                {events.length === 0 ? (
                    <Typography sx={{ color: '#666', fontSize: '0.75rem', px: 1.5, pb: 1.5 }}>
                        No fire tone-outs detected yet.
                    </Typography>
                ) : (
                    <List dense disablePadding sx={{ maxHeight: 200, overflowY: 'auto' }}>
                        {events.map(e => {
                            const clickable = !!e.transmissionId && !!onEventClick;
                            return (
                                <ListItem
                                    key={e.id}
                                    onClick={clickable ? () => onEventClick!(e) : undefined}
                                    title={clickable ? 'Jump to this recording in the history' : undefined}
                                    sx={{
                                        px: 1.5, py: 0.5, borderTop: '1px solid #161616',
                                        cursor: clickable ? 'pointer' : 'default',
                                        transition: 'background-color 0.15s',
                                        '&:hover': clickable ? { bgcolor: 'rgba(255,183,77,0.08)' } : undefined,
                                    }}
                                >
                                    <Box display="flex" alignItems="center" gap={1} width="100%">
                                        <Typography
                                            variant="caption"
                                            sx={{
                                                color: '#ffb74d', fontWeight: 'bold', fontSize: '9px',
                                                bgcolor: 'transparent', border: '1px solid #8a5a2b',
                                                px: 0.6, py: 0.1, borderRadius: 0.5, letterSpacing: 0.5, flexShrink: 0,
                                            }}
                                        >
                                            TONE
                                        </Typography>
                                        <Box flexGrow={1} minWidth={0}>
                                            <Typography sx={{ color: '#eee', fontSize: '0.8rem', lineHeight: 1.2 }} noWrap>
                                                {e.label}
                                            </Typography>
                                            <Typography sx={{ color: '#999', fontSize: '0.7rem' }} noWrap>
                                                {formatDetail(e)}
                                                {e.alphaTag ? ` · ${e.alphaTag}` : e.frequency ? ` · ${e.frequency.toFixed(4)} MHz` : ''}
                                            </Typography>
                                        </Box>
                                        {clickable && (
                                            <LaunchIcon sx={{ color: '#8a5a2b', fontSize: 14, flexShrink: 0 }} />
                                        )}
                                        <Typography sx={{ color: '#666', fontSize: '0.7rem', flexShrink: 0 }}>
                                            {new Date(e.timestamp).toLocaleTimeString()}
                                        </Typography>
                                    </Box>
                                </ListItem>
                            );
                        })}
                    </List>
                )}
            </Collapse>
        </Box>
    );
}
