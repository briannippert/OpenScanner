import { useState } from 'react';
import { Box, Typography, IconButton, Collapse, List, ListItemButton, Tooltip, ButtonBase } from '@mui/material';
import { alpha } from '@mui/material/styles';
import NotificationsActiveIcon from '@mui/icons-material/NotificationsActive';
import DeleteSweepIcon from '@mui/icons-material/DeleteSweep';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import ExpandLessIcon from '@mui/icons-material/ExpandLess';
import LaunchIcon from '@mui/icons-material/Launch';
import type { RadioEvent } from '../types';
import { status } from '../theme/tokens';
import EmptyState from './common/EmptyState';

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
        <Box sx={{ borderBottom: '1px solid', borderColor: 'surface.border', flexShrink: 0 }}>
            <Box display="flex" alignItems="center" justifyContent="space-between" sx={{ pr: 1 }}>
                {/* Accessible toggle: real button with aria-expanded + keyboard support. */}
                <ButtonBase
                    onClick={() => setOpen(o => !o)}
                    aria-expanded={open}
                    aria-label={`Fire tone-outs, ${events.length} events. ${open ? 'Collapse' : 'Expand'}`}
                    sx={{ flexGrow: 1, justifyContent: 'flex-start', px: 1.5, py: 1, borderRadius: 1 }}
                >
                    <Box display="flex" alignItems="center" gap={1} width="100%">
                        <NotificationsActiveIcon sx={{ color: status.warn, fontSize: 18 }} />
                        <Typography sx={{ color: 'text.primary', fontWeight: 700, fontSize: '0.8rem', letterSpacing: 0.5 }}>
                            FIRE TONE-OUTS
                        </Typography>
                        <Typography sx={{ color: 'text.secondary', fontSize: '0.75rem' }}>({events.length})</Typography>
                        <Box flexGrow={1} />
                        {open ? <ExpandLessIcon fontSize="small" sx={{ color: 'text.secondary' }} /> : <ExpandMoreIcon fontSize="small" sx={{ color: 'text.secondary' }} />}
                    </Box>
                </ButtonBase>
                {events.length > 0 && (
                    <Tooltip title="Clear events">
                        <IconButton size="small" onClick={onClear} sx={{ color: 'text.secondary' }} aria-label="Clear events">
                            <DeleteSweepIcon fontSize="small" />
                        </IconButton>
                    </Tooltip>
                )}
            </Box>

            <Collapse in={open} timeout="auto" unmountOnExit>
                {events.length === 0 ? (
                    <EmptyState dense icon={<NotificationsActiveIcon />} title="No fire tone-outs yet" hint="Detected tone-outs will appear here." />
                ) : (
                    <List dense disablePadding sx={{ maxHeight: 200, overflowY: 'auto' }}>
                        {events.map(e => {
                            const clickable = !!e.transmissionId && !!onEventClick;
                            return (
                                <ListItemButton
                                    key={e.id}
                                    disableRipple={!clickable}
                                    onClick={clickable ? () => onEventClick!(e) : undefined}
                                    title={clickable ? 'Jump to this recording in the history' : undefined}
                                    sx={{
                                        px: 1.5, py: 0.5, borderTop: '1px solid', borderColor: 'surface.base',
                                        cursor: clickable ? 'pointer' : 'default',
                                        '&:hover': clickable ? { bgcolor: alpha(status.warn, 0.08) } : { bgcolor: 'transparent' },
                                    }}
                                >
                                    <Box display="flex" alignItems="center" gap={1} width="100%">
                                        <Typography
                                            variant="caption"
                                            sx={{
                                                color: status.warn, fontWeight: 'bold', fontSize: '9px',
                                                border: '1px solid', borderColor: alpha(status.warn, 0.5),
                                                px: 0.6, py: 0.1, borderRadius: 0.5, letterSpacing: 0.5, flexShrink: 0,
                                            }}
                                        >
                                            TONE
                                        </Typography>
                                        <Box flexGrow={1} minWidth={0}>
                                            <Typography sx={{ color: 'text.primary', fontSize: '0.8rem', lineHeight: 1.2 }} noWrap>
                                                {e.label}
                                            </Typography>
                                            <Typography sx={{ color: 'text.secondary', fontSize: '0.7rem' }} noWrap>
                                                {formatDetail(e)}
                                                {e.alphaTag ? ` · ${e.alphaTag}` : e.frequency ? ` · ${e.frequency.toFixed(4)} MHz` : ''}
                                            </Typography>
                                        </Box>
                                        {clickable && <LaunchIcon sx={{ color: alpha(status.warn, 0.6), fontSize: 14, flexShrink: 0 }} />}
                                        <Typography sx={{ color: 'text.disabled', fontSize: '0.7rem', flexShrink: 0 }}>
                                            {new Date(e.timestamp).toLocaleTimeString()}
                                        </Typography>
                                    </Box>
                                </ListItemButton>
                            );
                        })}
                    </List>
                )}
            </Collapse>
        </Box>
    );
}
