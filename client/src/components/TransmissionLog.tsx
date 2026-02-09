import React, { useState, useEffect } from 'react';
import { 
    Box, 
    List, 
    ListItem, 
    ListItemText, 
    ListItemButton, 
    Collapse, 
    Typography, 
    IconButton, 
    Divider, 
    TextField, 
    CircularProgress,
    InputAdornment
} from '@mui/material';
import { 
    ExpandLess, 
    ExpandMore, 
    PlayCircleOutline, 
    StopCircle, 
        Delete,
        Download,
        Folder,
        Search,
    
    CalendarMonth,
    Today,
    Radio,
    History as HistoryIcon
} from '@mui/icons-material';
import type { CallLog, Channel } from '../types';

interface LogNodeProps {
    playingId: string | null;
    onPlay: (id: string, path: string, duration?: number) => void;
    onDelete: (id: string) => void;
}

interface Props extends LogNodeProps {
    liveLogs: CallLog[];
}

const TransmissionLog: React.FC<Props> = ({ liveLogs, playingId, onPlay, onDelete }) => {
    const [searchQuery, setSearchQuery] = useState('');
    const [searchResults, setSearchResults] = useState<CallLog[] | null>(null);
    const [years, setYears] = useState<string[]>([]);
    const [loading, setLoading] = useState(false);
    const [recentOpen, setRecentOpen] = useState(true);

    // Initial load of years
    useEffect(() => {
        fetch('/api/history/years')
            .then(res => res.json())
            .then(data => setYears(data))
            .catch(err => console.error("Failed to fetch years:", err));
    }, []);

    // Search handler
    useEffect(() => {
        const timer = setTimeout(() => {
            if (!searchQuery.trim()) {
                setSearchResults(null);
                return;
            }

            setLoading(true);
            fetch(`/api/history/search?q=${encodeURIComponent(searchQuery)}`)
                .then(res => res.json())
                .then(data => {
                    setSearchResults(data);
                    setLoading(false);
                })
                .catch(err => {
                    console.error("Search failed:", err);
                    setLoading(false);
                });
        }, 500);

        return () => clearTimeout(timer);
    }, [searchQuery]);

    return (
        <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column' }}>
            <Box sx={{ p: 2, borderBottom: '1px solid #222' }}>
                <TextField 
                    fullWidth 
                    variant="outlined" 
                    size="small" 
                    placeholder="Search logs..." 
                    value={searchQuery}
                    onChange={(e) => setSearchQuery(e.target.value)}
                    slotProps={{
                        input: {
                            startAdornment: (
                                <InputAdornment position="start">
                                    <Search sx={{ color: 'text.secondary' }} />
                                </InputAdornment>
                            ),
                            sx: { color: 'white' }
                        }
                    }}
                    sx={{
                        '& .MuiOutlinedInput-root': {
                            bgcolor: '#151515',
                            '& fieldset': { borderColor: '#333' },
                            '&:hover fieldset': { borderColor: '#555' },
                            '&.Mui-focused fieldset': { borderColor: '#00ff00' },
                        }
                    }}
                />
            </Box>

            <Box sx={{ flexGrow: 1, overflowY: 'auto' }}>
                {loading && <Box sx={{ p: 2, textAlign: 'center' }}><CircularProgress size={20} /></Box>}
                
                {searchResults ? (
                    <List dense>
                        {searchResults.length === 0 && !loading && (
                            <Typography variant="body2" sx={{ p: 2, textAlign: 'center', color: '#666' }}>
                                No results found.
                            </Typography>
                        )}
                        {searchResults.map(log => (
                            <LogItem key={log.id} log={log} playingId={playingId} onPlay={onPlay} onDelete={onDelete} />
                        ))}
                    </List>
                ) : (
                    <List dense component="nav">
                        {/* Recent Activity Node */}
                        <ListItemButton onClick={() => setRecentOpen(!recentOpen)} sx={{ borderBottom: '1px solid #1a1a1a', bgcolor: 'rgba(0, 255, 0, 0.05)' }}>
                            <HistoryIcon sx={{ mr: 2, color: '#00ff00', fontSize: 20 }} />
                            <ListItemText primary="Recent Activity" primaryTypographyProps={{ fontWeight: 'bold', color: '#00ff00' }} />
                            {recentOpen ? <ExpandLess /> : <ExpandMore />}
                        </ListItemButton>
                        <Collapse in={recentOpen} timeout="auto" unmountOnExit>
                            <List component="div" disablePadding>
                                {liveLogs.length === 0 && (
                                    <Typography variant="body2" sx={{ p: 2, textAlign: 'center', color: '#666' }}>
                                        No recent activity.
                                    </Typography>
                                )}
                                {liveLogs.map(log => (
                                    <LogItem key={log.id} log={log} playingId={playingId} onPlay={onPlay} onDelete={onDelete} />
                                ))}
                            </List>
                        </Collapse>

                        {/* Historical Tree */}
                        {years.map(year => (
                            <YearNode key={year} year={year} playingId={playingId} onPlay={onPlay} onDelete={onDelete} />
                        ))}
                    </List>
                )}
            </Box>
        </Box>
    );
};

const YearNode = ({ year, playingId, onPlay, onDelete }: { year: string } & LogNodeProps) => {
    const [open, setOpen] = useState(false);
    const [months, setMonths] = useState<string[]>([]);
    const [loaded, setLoaded] = useState(false);

    const handleToggle = () => {
        if (!open && !loaded) {
            fetch(`/api/history/${year}/months`)
                .then(res => res.json())
                .then(data => {
                    setMonths(data);
                    setLoaded(true);
                })
                .catch(err => console.error(`Failed to fetch months for ${year}:`, err));
        }
        setOpen(!open);
    };

    return (
        <>
            <ListItemButton onClick={handleToggle} sx={{ pl: 2, borderBottom: '1px solid #1a1a1a' }}>
                <Folder sx={{ mr: 2, color: '#444', fontSize: 20 }} />
                <ListItemText primary={year} primaryTypographyProps={{ fontWeight: 'bold' }} />
                {open ? <ExpandLess /> : <ExpandMore />}
            </ListItemButton>
            <Collapse in={open} timeout="auto" unmountOnExit>
                <List component="div" disablePadding>
                    {months.map(month => (
                        <MonthNode key={month} year={year} month={month} playingId={playingId} onPlay={onPlay} onDelete={onDelete} />
                    ))}
                </List>
            </Collapse>
        </>
    );
};

const MonthNode = ({ year, month, playingId, onPlay, onDelete }: { year: string; month: string } & LogNodeProps) => {
    const [open, setOpen] = useState(false);
    const [days, setDays] = useState<string[]>([]);
    const [loaded, setLoaded] = useState(false);

    const monthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    const monthName = monthNames[parseInt(month) - 1] || month;

    const handleToggle = () => {
        if (!open && !loaded) {
            fetch(`/api/history/${year}/${month}/days`)
                .then(res => res.json())
                .then(data => {
                    setDays(data);
                    setLoaded(true);
                })
                .catch(err => console.error(`Failed to fetch days for ${year}-${month}:`, err));
        }
        setOpen(!open);
    };

    return (
        <>
            <ListItemButton onClick={handleToggle} sx={{ pl: 4, borderBottom: '1px solid #1a1a1a' }}>
                <CalendarMonth sx={{ mr: 2, color: '#444', fontSize: 18 }} />
                <ListItemText primary={monthName} />
                {open ? <ExpandLess /> : <ExpandMore />}
            </ListItemButton>
            <Collapse in={open} timeout="auto" unmountOnExit>
                <List component="div" disablePadding>
                    {days.map(day => (
                        <DayNode key={day} year={year} month={month} day={day} playingId={playingId} onPlay={onPlay} onDelete={onDelete} />
                    ))}
                </List>
            </Collapse>
        </>
    );
};

const DayNode = ({ year, month, day, playingId, onPlay, onDelete }: { year: string; month: string; day: string } & LogNodeProps) => {
    const [open, setOpen] = useState(false);
    const [channels, setChannels] = useState<Channel[]>([]);
    const [loaded, setLoaded] = useState(false);

    const handleToggle = () => {
        if (!open && !loaded) {
            fetch(`/api/history/${year}/${month}/${day}/channels`)
                .then(res => res.json())
                .then(data => {
                    setChannels(data);
                    setLoaded(true);
                })
                .catch(err => console.error(`Failed to fetch channels for ${year}-${month}-${day}:`, err));
        }
        setOpen(!open);
    };

    return (
        <>
            <ListItemButton onClick={handleToggle} sx={{ pl: 6, borderBottom: '1px solid #1a1a1a' }}>
                <Today sx={{ mr: 2, color: '#444', fontSize: 18 }} />
                <ListItemText primary={`Day ${day}`} />
                {open ? <ExpandLess /> : <ExpandMore />}
            </ListItemButton>
            <Collapse in={open} timeout="auto" unmountOnExit>
                <List component="div" disablePadding>
                    {channels.map((ch, idx) => (
                        <ChannelNode 
                            key={`${ch.frequency}-${idx}`} 
                            year={year} month={month} day={day} 
                            channel={ch} 
                            playingId={playingId} onPlay={onPlay} onDelete={onDelete} 
                        />
                    ))}
                </List>
            </Collapse>
        </>
    );
};

const ChannelNode = ({ year, month, day, channel, playingId, onPlay, onDelete }: { year: string; month: string; day: string; channel: Channel } & LogNodeProps) => {
    const [open, setOpen] = useState(false);
    const [logs, setLogs] = useState<CallLog[]>([]);
    const [loaded, setLoaded] = useState(false);

    const handleToggle = () => {
        if (!open && !loaded) {
            fetch(`/api/history/filter?year=${year}&month=${month}&day=${day}&alphaTag=${encodeURIComponent(channel.alphaTag)}&frequency=${channel.frequency}`)
                .then(res => res.json())
                .then(data => {
                    setLogs(data);
                    setLoaded(true);
                })
                .catch(err => console.error(`Failed to filter history for ${channel.alphaTag}:`, err));
        }
        setOpen(!open);
    };

    return (
        <>
            <ListItemButton onClick={handleToggle} sx={{ pl: { xs: 4, sm: 8 }, borderBottom: '1px solid #1a1a1a', bgcolor: open ? 'rgba(255,255,255,0.02)' : 'transparent' }}>
                <Radio sx={{ mr: 2, color: '#00ff00', fontSize: 16 }} />
                <ListItemText 
                    primary={channel.alphaTag || `${channel.frequency} MHz`} 
                    secondary={channel.alphaTag ? `${channel.frequency} MHz` : null}
                    primaryTypographyProps={{ fontSize: '0.9rem', color: '#eee' }}
                    secondaryTypographyProps={{ fontSize: '0.75rem', color: '#888' }}
                />
                {open ? <ExpandLess /> : <ExpandMore />}
            </ListItemButton>
            <Collapse in={open} timeout="auto" unmountOnExit>
                <List component="div" disablePadding>
                    {logs.map(log => (
                        <LogItem key={log.id} log={log} playingId={playingId} onPlay={onPlay} onDelete={onDelete} />
                    ))}
                </List>
            </Collapse>
        </>
    );
};

const LogItem = ({ log, playingId, onPlay, onDelete }: { log: CallLog } & LogNodeProps) => {
    return (
        <React.Fragment>
            <ListItem 
                sx={{ 
                    pl: { xs: 4, sm: 10 }, 
                    pr: 2,
                    py: 1,
                    '& .MuiListItemSecondaryAction-root': {
                        right: 8
                    },
                    bgcolor: 'rgba(0,0,0,0.2)'
                }}
                secondaryAction={
                    <Box sx={{ display: 'flex', gap: 0.5 }}>
                        {log.audio_path && (
                            <>
                                <IconButton size="small" onClick={() => onPlay(log.id, log.audio_path!, log.duration)}>
                                    {playingId === log.id 
                                        ? <StopCircle sx={{ color: 'error.main', fontSize: 20 }} />
                                        : <PlayCircleOutline sx={{ color: 'primary.main', fontSize: 20 }} />
                                    }
                                </IconButton>
                                <IconButton 
                                    size="small" 
                                    component="a" 
                                    href={`/audio/${log.audio_path}`} 
                                    download={log.audio_path.split('/').pop() || 'recording.wav'}
                                    title="Download recording"
                                >
                                    <Download sx={{ color: 'primary.main', fontSize: 20, opacity: 0.7 }} />
                                </IconButton>
                            </>
                        )}
                        <IconButton size="small" onClick={() => onDelete(log.id)}>
                            <Delete sx={{ color: '#444', fontSize: 18 }} />
                        </IconButton>
                    </Box>
                }
            >
                <ListItemText 
                    sx={{ pr: 20 }}
                    primary={
                        <Box display="flex" alignItems="center" gap={1} flexWrap="wrap">
                            <Typography variant="caption" sx={{ fontFamily: 'monospace', color: '#aaa' }}>
                                {new Date(log.timestamp.endsWith('Z') ? log.timestamp : log.timestamp + 'Z').toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}
                            </Typography>
                            <Typography variant="caption" sx={{ color: '#fff', fontWeight: 'bold', fontSize: '0.8rem' }}>
                                {log.alphaTag}
                            </Typography>
                            {log.detectedTone && (
                                <Typography variant="caption" sx={{ color: '#ff0000', fontWeight: 'bold', fontSize: '9px', border: '1px solid #ff0000', px: 0.5, borderRadius: 0.5 }}>
                                    {log.detectedTone}
                                </Typography>
                            )}
                            {log.duration && (
                                <Typography variant="caption" sx={{ color: '#555', fontSize: '9px' }}>
                                    {log.duration.toFixed(1)}s
                                </Typography>
                            )}
                        </Box>
                    }
                    secondary={
                        <Box component="span" display="block">
                            {log.transcription && (
                                <Typography variant="body2" sx={{ color: '#ccc', fontStyle: 'italic', fontSize: '11px', mt: 0.5 }}>
                                    "{log.transcription}"
                                </Typography>
                            )}
                            <Box display="flex" gap={1} mt={0.5}>
                                {log.sourceID && (
                                    <Typography variant="caption" sx={{ color: log.sourceID < 100 ? '#00ffff' : '#ffaa00', fontSize: '10px' }}>
                                        {log.sourceID < 100 ? `BASE` : `UNIT ${log.sourceID}`}
                                    </Typography>
                                )}
                                {log.lat && log.lat !== 0 && (
                                    <Typography variant="caption" sx={{ color: '#444', fontSize: '10px' }}>
                                        {log.lat.toFixed(3)}, {log.lon?.toFixed(3)}
                                    </Typography>
                                )}
                            </Box>
                        </Box>
                    }
                    secondaryTypographyProps={{ component: 'div' }}
                />
            </ListItem>
            <Divider component="li" sx={{ borderColor: '#111' }} />
        </React.Fragment>
    );
};

export default TransmissionLog;
