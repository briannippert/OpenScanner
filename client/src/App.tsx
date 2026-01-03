import { useEffect, useState, useRef } from 'react';
import { AppBar, Toolbar, Typography, CssBaseline, ThemeProvider, createTheme, Box, Card, CardActionArea, Grid, List, ListItem, ListItemText, Divider, Paper, Chip, IconButton, Snackbar, Alert, Tooltip } from '@mui/material';
import ScannerDisplay from './components/ScannerDisplay';
import ChannelManager from './components/ChannelManager';
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import PauseIcon from '@mui/icons-material/Pause';
import HistoryIcon from '@mui/icons-material/History';
import AssessmentIcon from '@mui/icons-material/Assessment';
import LocationOnIcon from '@mui/icons-material/LocationOn';
import SpeedIcon from '@mui/icons-material/Speed';
import SatelliteAltIcon from '@mui/icons-material/SatelliteAlt';
import AccessTimeIcon from '@mui/icons-material/AccessTime';
import UsbIcon from '@mui/icons-material/Usb';
import UsbOffIcon from '@mui/icons-material/UsbOff';
import PlayCircleOutlineIcon from '@mui/icons-material/PlayCircleOutline';
import StopCircleIcon from '@mui/icons-material/StopCircle';
import DeleteIcon from '@mui/icons-material/Delete';
import EditIcon from '@mui/icons-material/Edit';
import FullscreenIcon from '@mui/icons-material/Fullscreen';
import FullscreenExitIcon from '@mui/icons-material/FullscreenExit';
import type { ScannerState, Channel, CallLog } from './types';

const darkTheme = createTheme({
  palette: {
    mode: 'dark',
    primary: {
      main: '#00ff00',
    },
    background: {
      default: '#000000',
      paper: '#0f0f0f',
    },
    text: {
      primary: '#e0e0e0',
      secondary: '#a0a0a0',
    }
  },
  typography: {
    fontFamily: '"Roboto Mono", monospace',
    h6: { letterSpacing: 1 },
    button: { letterSpacing: 1.5 },
  },
  components: {
    MuiCard: {
        styleOverrides: {
            root: {
                backgroundImage: 'none',
                backgroundColor: '#111',
                border: '1px solid #222',
            }
        }
    },
    MuiAppBar: {
        styleOverrides: {
            root: {
                backgroundColor: '#050505',
                borderBottom: '1px solid #222',
            }
        }
    }
  }
});

function App() {
  const [currentTime, setCurrentTime] = useState(new Date());
  const [scannerState, setScannerState] = useState<ScannerState>({ status: 'IDLE', signalStrength: 0 });
  const [channels, setChannels] = useState<Channel[]>([]);
  const [callLog, setCallLog] = useState<CallLog[]>([]);
  const [audioAnalyser, setAudioAnalyser] = useState<AnalyserNode | undefined>(undefined);
  const [isManagerOpen, setIsManagerOpen] = useState(false);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);
  const [isConnected, setIsConnected] = useState(false);
  const [playingId, setPlayingId] = useState<string | null>(null);
  const [isFullscreen, setIsFullscreen] = useState(false);
  const ws = useRef<WebSocket | null>(null);
  const wakeLock = useRef<any>(null);
  const activeSource = useRef<AudioBufferSourceNode | null>(null);

  const manualHold = scannerState.manualHoldFrequency;

  // Fullscreen Manager
  useEffect(() => {
    const handleFullscreenChange = () => {
      setIsFullscreen(!!document.fullscreenElement);
    };
    document.addEventListener('fullscreenchange', handleFullscreenChange);
    return () => document.removeEventListener('fullscreenchange', handleFullscreenChange);
  }, []);

  const toggleFullscreen = () => {
    if (!document.fullscreenElement) {
      document.documentElement.requestFullscreen().catch(err => {
        console.error(`Error attempting to enable full-screen mode: ${err.message} (${err.name})`);
      });
    } else {
      document.exitFullscreen();
    }
  };

  // Wake Lock Manager
  useEffect(() => {
    const requestWakeLock = async () => {
      if ('wakeLock' in navigator && scannerState.status !== 'IDLE') {
        try {
          if (!wakeLock.current) {
            wakeLock.current = await (navigator as any).wakeLock.request('screen');
            console.log('Wake Lock active');
            wakeLock.current.addEventListener('release', () => {
               wakeLock.current = null;
               console.log('Wake Lock released');
            });
          }
        } catch (err: any) {
          console.error(`${err.name}, ${err.message}`);
        }
      } else if (wakeLock.current && scannerState.status === 'IDLE') {
         wakeLock.current.release();
         wakeLock.current = null;
      }
    };

    requestWakeLock();

    // Re-acquire on visibility change if still active
    const handleVisibilityChange = () => {
       if (document.visibilityState === 'visible' && scannerState.status !== 'IDLE') {
         requestWakeLock();
       }
    };
    
    document.addEventListener('visibilitychange', handleVisibilityChange);
    return () => {
      document.removeEventListener('visibilitychange', handleVisibilityChange);
      if (wakeLock.current) wakeLock.current.release();
    };
  }, [scannerState.status]);

  // Clock Effect
  useEffect(() => {
    const timer = setInterval(() => setCurrentTime(new Date()), 1000);
    return () => clearInterval(timer);
  }, []);

  // Helper to send commands
  const sendCommand = (action: string, frequency?: number, value?: number) => {
    const isDev = window.location.port === '5173';
    const port = isDev ? '3001' : window.location.port || '80';
    const protocol = window.location.protocol;
    const backendHost = window.location.hostname;
    const portSuffix = (port === '80' || port === '') ? '' : `:${port}`;
    const httpUrl = `${protocol}//${backendHost}${portSuffix}/api/control`;
    
    fetch(httpUrl, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ action, frequency, value })
    }).catch(err => console.error("Command failed:", err));
  };

  const handleChannelClick = (ch: Channel) => {
      // Resume audio context on user interaction
      if (window.audioCtx && window.audioCtx.state === 'suspended') {
          window.audioCtx.resume();
      }

      if (manualHold === ch.frequency) {
          sendCommand('scan');
      } else {
          sendCommand('hold', ch.frequency);
      }
  };

  const nextStartTime = useRef<number>(0);

  // Audio Playback Helper for recorded files
  const playRawAudio = async (id: string, filename: string) => {
    if (!window.audioCtx) {
        window.audioCtx = new (window.AudioContext || (window as any).webkitAudioContext)({ sampleRate: 48000 });
    }
    
    if (window.audioCtx.state === 'suspended') {
        await window.audioCtx.resume();
    }

    if (playingId === id && activeSource.current) {
        activeSource.current.stop();
        activeSource.current = null;
        setPlayingId(null);
        return;
    }

    // Stop any current playback
    if (activeSource.current) {
        activeSource.current.stop();
        activeSource.current = null;
    }

    try {
        const response = await fetch(`/audio/${filename}`);
        if (!response.ok) {
            console.error(`Failed to load audio: ${response.status} ${response.statusText}`);
            return;
        }
        const arrayBuffer = await response.arrayBuffer();
        const int16Array = new Int16Array(arrayBuffer);
        console.log(`Playing audio: ${filename}, size: ${int16Array.length} samples`);
        
        const float32Array = new Float32Array(int16Array.length);
        for (let i = 0; i < int16Array.length; i++) {
            float32Array[i] = int16Array[i] / 32768;
        }

        // Recorded files are 8000Hz s16le
        const buffer = window.audioCtx.createBuffer(1, float32Array.length, 8000);
        buffer.copyToChannel(float32Array, 0);
        const source = window.audioCtx.createBufferSource();
        source.buffer = buffer;
        source.connect(window.audioCtx.destination);
        
        activeSource.current = source;
        setPlayingId(id);

        source.onended = () => {
            if (activeSource.current === source) {
                activeSource.current = null;
                setPlayingId(null);
            }
        };

        source.start();
    } catch (e) {
        console.error("Playback failed:", e);
        setPlayingId(null);
    }
  };

  const deleteEntry = async (id: string) => {
    const isDev = window.location.port === '5173';
    const port = isDev ? '3001' : window.location.port || '80';
    const protocol = window.location.protocol;
    const backendHost = window.location.hostname;
    const portSuffix = (port === '80' || port === '') ? '' : `:${port}`;
    const deleteUrl = `${protocol}//${backendHost}${portSuffix}/api/history/${id}`;

    try {
        await fetch(deleteUrl, { method: 'DELETE' });
        setCallLog(prev => prev.filter(log => log.id !== id));
    } catch (e) {
        console.error("Delete failed:", e);
    }
  };

  const refreshChannels = () => {
    const isDev = window.location.port === '5173';
    const port = isDev ? '3001' : window.location.port || '80';
    const protocol = window.location.protocol;
    const backendHost = window.location.hostname;
    const portSuffix = (port === '80' || port === '') ? '' : `:${port}`;
    fetch(`${protocol}//${backendHost}${portSuffix}/api/channels`)
      .then(res => res.json())
      .then(data => setChannels(data))
      .catch(err => console.error("Failed to fetch channels:", err));
  };

  const handleSaveChannel = async (channel: Channel) => {
    const isDev = window.location.port === '5173';
    const port = isDev ? '3001' : window.location.port || '80';
    const protocol = window.location.protocol;
    const backendHost = window.location.hostname;
    const portSuffix = (port === '80' || port === '') ? '' : `:${port}`;
    const baseUrl = `${protocol}//${backendHost}${portSuffix}/api/channels`;

    const method = channel.id ? 'PUT' : 'POST';
    const url = channel.id ? `${baseUrl}/${channel.id}` : baseUrl;

    try {
        await fetch(url, {
            method,
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(channel)
        });
        refreshChannels();
    } catch (e) {
        console.error("Save channel failed:", e);
    }
  };

  const handleDeleteChannel = async (id: number) => {
    const isDev = window.location.port === '5173';
    const port = isDev ? '3001' : window.location.port || '80';
    const protocol = window.location.protocol;
    const backendHost = window.location.hostname;
    const portSuffix = (port === '80' || port === '') ? '' : `:${port}`;
    
    try {
        await fetch(`${protocol}//${backendHost}${portSuffix}/api/channels/${id}`, { method: 'DELETE' });
        refreshChannels();
    } catch (e) {
        console.error("Delete channel failed:", e);
    }
  };

  useEffect(() => {
    const isDev = window.location.port === '5173';
    const port = isDev ? '3001' : window.location.port || '80';
    const protocol = window.location.protocol;
    const wsProtocol = protocol === 'https:' ? 'wss:' : 'ws:';
    const backendHost = window.location.hostname;
    
    const portSuffix = (port === '80' || port === '') ? '' : `:${port}`;

    const httpUrl = `${protocol}//${backendHost}${portSuffix}/api/channels`;
    const historyUrl = `${protocol}//${backendHost}${portSuffix}/api/history`;
    const wsUrl = `${wsProtocol}//${backendHost}${portSuffix}/ws`;

    fetch(httpUrl)
      .then(res => res.json())
      .then(data => setChannels(data))
      .catch(err => console.error("Failed to fetch channels:", err));

    fetch(historyUrl)
      .then(res => res.json())
      .then(data => setCallLog(data))
      .catch(err => console.error("Failed to fetch history:", err));

    const connectWs = () => {
        ws.current = new WebSocket(wsUrl);
        
        ws.current.onopen = () => {
            setIsConnected(true);
            console.log("WebSocket Connected");
        };

        ws.current.onclose = () => {
            setIsConnected(false);
            console.log("WebSocket Disconnected, retrying in 3s...");
            setTimeout(connectWs, 3000);
        };

        ws.current.onmessage = async (event) => {
          if (event.data instanceof Blob) {
            // Handle Audio
            // console.log("Audio Packet Received:", event.data.size, "bytes");
            const arrayBuffer = await event.data.arrayBuffer();
            const int16Array = new Int16Array(arrayBuffer);
            const float32Array = new Float32Array(int16Array.length);
            for (let i = 0; i < int16Array.length; i++) {
                float32Array[i] = int16Array[i] / 32768;
            }
            
            if (!window.audioCtx) {
                window.audioCtx = new (window.AudioContext || (window as any).webkitAudioContext)({ sampleRate: 48000 });
                const analyser = window.audioCtx.createAnalyser();
                analyser.fftSize = 1024;
                analyser.connect(window.audioCtx.destination);
                setAudioAnalyser(analyser);
                (window.audioCtx as any)._analyser = analyser;
            }
            
            const ctx = window.audioCtx;
            if (ctx.state === 'suspended') ctx.resume();
            const analyser = (ctx as any)._analyser;

            const audioBuffer = ctx.createBuffer(1, float32Array.length, 8000);
            audioBuffer.copyToChannel(float32Array, 0);

            const source = ctx.createBufferSource();
            source.buffer = audioBuffer;
            source.connect(analyser);
            
            // Scheduler to prevent crackling/overlaps
            const currentTime = ctx.currentTime;
            const JITTER_BUFFER = 0.35; // 350ms buffer for stability

            if (nextStartTime.current < currentTime) {
                nextStartTime.current = currentTime + JITTER_BUFFER;
            }
            
            source.start(nextStartTime.current);
            nextStartTime.current += audioBuffer.duration;
          } else {
            // Handle JSON
            try {
                const message = JSON.parse(event.data);
                if (message.type === 'STATE_UPDATE') {
                  const newState = message.payload as ScannerState;
                  setScannerState(newState);
                } else if (message.type === 'NEW_LOG') {
                  const newEntry = message.payload as CallLog;
                  setCallLog(log => [newEntry, ...log].slice(0, 100));
                } else if (message.type === 'ERROR') {
                  setErrorMsg(message.payload);
                }
            } catch (e) {
                console.warn('Unknown message:', event.data);
            }
          }
        };
    };

    connectWs();

    const resumeAudio = () => {
        if (window.audioCtx && window.audioCtx.state === 'suspended') {
            window.audioCtx.resume();
        }
    };
    window.addEventListener('click', resumeAudio);

    return () => {
      window.removeEventListener('click', resumeAudio);
      if (ws.current) ws.current.close();
    };
  }, []);

  return (
    <ThemeProvider theme={darkTheme}>
      <CssBaseline />
      <Box sx={{ 
          display: 'flex', 
          flexDirection: 'column', 
          height: '100vh', 
          width: '100vw',
          bgcolor: 'background.default',
          overflow: 'hidden',
          position: 'relative'
      }}>
        
        {!isConnected && (
            <Box sx={{
                position: 'absolute',
                top: 0, left: 0, right: 0, bottom: 0,
                bgcolor: 'rgba(0,0,0,0.8)',
                zIndex: 9999,
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                backdropFilter: 'blur(4px)'
            }}>
                <Paper sx={{ p: 4, textAlign: 'center', border: '1px solid #ff0000', bgcolor: '#111' }}>
                    <Typography variant="h5" color="error" fontWeight="bold" gutterBottom>
                        CONNECTION LOST
                    </Typography>
                    <Typography color="textSecondary">
                        Attempting to reconnect to OpenScanner server...
                    </Typography>
                </Paper>
            </Box>
        )}

        {/* Header */}
        <AppBar position="static" elevation={0}>
            <Toolbar variant="dense">
                <Typography variant="h6" component="div" sx={{ flexGrow: 1, color: '#00ff00', fontWeight: '900', letterSpacing: 3 }}>
                    OPENSCANNER <span style={{fontSize: '0.7em', color: '#666'}}>P25</span>
                </Typography>

                <Box sx={{ mr: 2 }}>
                    <Tooltip title={scannerState.isHardwareConnected ? `${scannerState.deviceName || 'RTL-SDR Device'} (${scannerState.devicePort || 'USB'})` : 'No Device Detected'}>
                        <Chip 
                            icon={scannerState.isHardwareConnected ? <UsbIcon /> : <UsbOffIcon />}
                            label={scannerState.isHardwareConnected ? "SDR READY" : "SDR DISCONNECTED"}
                            size="small"
                            color={scannerState.isHardwareConnected ? "success" : "error"}
                            variant="outlined"
                            sx={{ fontWeight: 'bold', fontSize: '10px' }}
                        />
                    </Tooltip>
                </Box>

                <Box sx={{ mr: 3, px: 2, py: 0.5, bgcolor: '#111', borderRadius: 1, border: '1px solid #333', display: 'flex', alignItems: 'center', gap: 1 }}>
                    {scannerState.gps?.time && scannerState.gps.fix >= 2 
                        ? <SatelliteAltIcon sx={{ fontSize: 16, color: 'primary.main' }} />
                        : <AccessTimeIcon sx={{ fontSize: 16, color: 'text.secondary' }} />
                    }
                    <Typography variant="caption" sx={{ fontFamily: 'monospace', color: 'primary.main', fontWeight: 'bold', fontSize: '14px' }}>
                        {scannerState.gps?.time && scannerState.gps.fix >= 2 
                            ? new Date(scannerState.gps.time).toLocaleTimeString([], { hour12: false })
                            : currentTime.toLocaleTimeString([], { hour12: false })
                        }
                    </Typography>
                </Box>
                
                {scannerState.gps && (
                    <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, mr: 3, px: 2, py: 0.5, bgcolor: '#111', borderRadius: 1, border: '1px solid #333' }}>
                        <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
                            <SatelliteAltIcon sx={{ fontSize: 16, color: (scannerState.gps.satsVisible || 0) > 0 ? 'primary.main' : '#444' }} />
                            <Typography variant="caption" sx={{ fontFamily: 'monospace', color: 'white' }}>
                                {scannerState.gps.sats}{scannerState.gps.satsVisible ? `/${scannerState.gps.satsVisible}` : ''} SATS
                            </Typography>
                        </Box>
                        
                        {scannerState.gps.fix < 2 ? (
                            <Typography variant="caption" sx={{ color: 'warning.main', fontWeight: 'bold', fontSize: '10px' }}>
                                NO FIX
                            </Typography>
                        ) : (
                            <>
                                <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
                                    <LocationOnIcon sx={{ fontSize: 16, color: 'primary.main' }} />
                                    <Typography variant="caption" sx={{ fontFamily: 'monospace', color: 'white' }}>
                                        {scannerState.gps.lat.toFixed(4)}, {scannerState.gps.lon.toFixed(4)}
                                    </Typography>
                                </Box>
                                <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
                                    <Typography variant="caption" sx={{ color: 'text.secondary', fontSize: '10px' }}>ALT</Typography>
                                    <Typography variant="caption" sx={{ fontFamily: 'monospace', color: 'white' }}>
                                        {Math.round(scannerState.gps.alt * 3.28084)}ft
                                    </Typography>
                                </Box>
                                <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
                                    <SpeedIcon sx={{ fontSize: 16, color: 'primary.main' }} />
                                    <Typography variant="caption" sx={{ fontFamily: 'monospace', color: 'white' }}>
                                        {Math.round(scannerState.gps.speed * 2.23694)} mph
                                    </Typography>
                                </Box>
                            </>
                        )}
                    </Box>
                )}

                {manualHold && (
                    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                        <Chip label="MANUAL HOLD" color="warning" size="small" icon={<PauseIcon />} />
                        <Chip 
                            label="RESUME SCAN" 
                            color="primary" 
                            size="small" 
                            onClick={() => {
                                sendCommand('scan');
                            }}
                            icon={<PlayArrowIcon />} 
                            sx={{ cursor: 'pointer' }}
                        />
                    </Box>
                )}

                <Tooltip title={isFullscreen ? "Exit Fullscreen" : "Fullscreen"}>
                    <IconButton color="inherit" onClick={toggleFullscreen} sx={{ ml: 1 }}>
                        {isFullscreen ? <FullscreenExitIcon /> : <FullscreenIcon />}
                    </IconButton>
                </Tooltip>
            </Toolbar>
        </AppBar>

        {/* Main Content Dashboard */}
        <Box sx={{ flexGrow: 1, p: 2, height: '100%', overflowY: { xs: 'auto', md: 'hidden' } }}>
            <Grid container spacing={2} sx={{ height: { xs: 'auto', md: '100%' } }}>
                
                {/* Left Column: Active Scanner & Channel Grid (Compact) */}
                <Grid size={{ xs: 12, md: 4, lg: 3 }} sx={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
                    
                    {/* Hero Widget */}
                    <Box sx={{ mb: 2 }}>
                        <ScannerDisplay 
                            state={scannerState} 
                            analyser={audioAnalyser}
                            channels={channels}
                            onScan={() => {
                                sendCommand('scan');
                            }}
                        />
                    </Box>

                    {/* Channel Grid */}
                    <Paper sx={{ flexGrow: 1, p: 2, bgcolor: '#0a0a0a', border: '1px solid #222', borderRadius: 2, overflowY: 'auto', minHeight: 0 }}>
                        <Box display="flex" alignItems="center" mb={2} justifyContent="space-between">
                            <Box display="flex" alignItems="center" gap={1}>
                                <AssessmentIcon color="primary" fontSize="small" />
                                <Typography variant="subtitle2" color="text.secondary" fontWeight="bold">CHANNEL CONTROL</Typography>
                            </Box>
                            <IconButton size="small" onClick={() => setIsManagerOpen(true)}>
                                <EditIcon fontSize="small" />
                            </IconButton>
                        </Box>
                        <Grid container spacing={2}>
                            {channels.map((ch) => (
                                <Grid size={{ xs: 12 }} key={ch.frequency}>
                                    <Card 
                                        sx={{ 
                                            border: manualHold === ch.frequency ? '1px solid #ff9800' : '1px solid #333',
                                            bgcolor: scannerState.currentChannel?.frequency === ch.frequency ? 'rgba(0, 255, 0, 0.05)' : '#151515',
                                            transition: 'all 0.2s',
                                            '&:hover': { bgcolor: '#222' }
                                        }}
                                    >
                                        <CardActionArea onClick={() => handleChannelClick(ch)} sx={{ p: 2 }}>
                                            <Box display="flex" justifyContent="space-between" alignItems="flex-start">
                                                <Box>
                                                    <Typography variant="subtitle1" fontWeight="bold" color={manualHold === ch.frequency ? 'warning.main' : 'text.primary'}>
                                                        {ch.alphaTag}
                                                    </Typography>
                                                    <Typography variant="caption" color="text.secondary">
                                                        {ch.description}
                                                    </Typography>
                                                </Box>
                                                <Typography variant="body2" sx={{ fontFamily: 'monospace', color: 'primary.main' }}>
                                                    {ch.frequency}
                                                </Typography>
                                            </Box>
                                            <Box mt={1} display="flex" alignItems="center" gap={1}>
                                                <Chip label={ch.mode} size="small" sx={{ height: 20, fontSize: '0.65rem', bgcolor: '#333' }} />
                                                <Box flexGrow={1} />
                                                {manualHold === ch.frequency ? <PauseIcon fontSize="small" color="warning" /> : <PlayArrowIcon fontSize="small" sx={{ opacity: 0.3 }} />}
                                            </Box>
                                        </CardActionArea>
                                    </Card>
                                </Grid>
                            ))}
                        </Grid>
                    </Paper>
                </Grid>

                {/* Right Column: Transmission Log (Expanded) */}
                <Grid size={{ xs: 12, md: 8, lg: 9 }} sx={{ height: '100%', overflow: 'hidden' }}>
                    <Paper sx={{ height: '100%', display: 'flex', flexDirection: 'column', bgcolor: '#0a0a0a', border: '1px solid #222', borderRadius: 2, overflowY: 'auto' }}>
                        <Box sx={{ p: 2, borderBottom: '1px solid #222' }} display="flex" alignItems="center" gap={1}>
                            <HistoryIcon color="primary" fontSize="small" />
                            <Typography variant="subtitle2" fontWeight="bold" color="text.secondary">TRANSMISSION LOG</Typography>
                        </Box>
                        <List dense sx={{ flexGrow: 1, overflowY: 'auto', p: 0 }}>
                            {callLog.length === 0 && (
                                <Box sx={{ p: 4, textAlign: 'center', color: '#444' }}>
                                    <Typography variant="body2">No Activity</Typography>
                                </Box>
                            )}
                            {callLog.map((log) => (
                                <div key={log.id}>
                                    <ListItem 
                                        sx={{ 
                                            px: 2, 
                                            py: 1,
                                            '& .MuiListItemSecondaryAction-root': {
                                                right: 8
                                            }
                                        }}
                                        secondaryAction={
                                            <Box sx={{ display: 'flex', gap: 0.5 }}>
                                                {log.audio_path && (
                                                    <IconButton size="small" onClick={() => playRawAudio(log.id, log.audio_path!)}>
                                                        {playingId === log.id 
                                                            ? <StopCircleIcon sx={{ color: 'error.main', fontSize: 22 }} />
                                                            : <PlayCircleOutlineIcon sx={{ color: 'primary.main', fontSize: 22 }} />
                                                        }
                                                    </IconButton>
                                                )}
                                                <IconButton size="small" onClick={() => deleteEntry(log.id)}>
                                                    <DeleteIcon sx={{ color: '#444', fontSize: 20 }} />
                                                </IconButton>
                                            </Box>
                                        }
                                    >
                                        <ListItemText 
                                            sx={{ pr: 8 }} // Add padding to prevent text overlap with buttons
                                            primary={
                                                <Typography variant="body2" fontWeight="bold" color="white">
                                                    {log.alphaTag}
                                                </Typography>
                                            }
                                            secondary={
                                                <Box component="span">
                                                    <Typography variant="caption" color="gray" sx={{ display: 'flex', justifyContent: 'space-between', mb: 0.5 }}>
                                                        <span>{log.frequency} MHz {log.sourceID && (
                                                            <span style={{ 
                                                                color: log.sourceID < 100 ? '#00ffff' : '#ffaa00',
                                                                marginLeft: '8px',
                                                                fontWeight: 'bold'
                                                            }}>
                                                                {log.sourceID < 100 ? `[BASE]` : `[UNIT ${log.sourceID}]`}
                                                            </span>
                                                        )}</span>
                                                        <span>{new Date(log.timestamp.endsWith('Z') ? log.timestamp : log.timestamp + 'Z').toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>
                                                    </Typography>
                                                    <Box sx={{ display: 'flex', gap: 1, alignItems: 'center' }}>
                                                        {log.lat && (
                                                            <Typography variant="caption" sx={{ color: '#444', fontSize: '9px' }}>
                                                                LOC: {log.lat.toFixed(3)}, {log.lon!.toFixed(3)}
                                                            </Typography>
                                                        )}
                                                        {log.lat && log.duration && <Typography variant="caption" sx={{ color: '#333', fontSize: '9px' }}>•</Typography>}
                                                        {log.duration && (
                                                            <Typography variant="caption" sx={{ color: '#444', fontSize: '9px' }}>
                                                                {log.duration.toFixed(1)}s
                                                            </Typography>
                                                        )}
                                                    </Box>
                                                    {log.transcription && (
                                                        <Typography variant="body2" sx={{ color: '#aaa', fontStyle: 'italic', mt: 0.5, fontSize: '11px', borderLeft: '2px solid #333', pl: 1 }}>
                                                            "{log.transcription}"
                                                        </Typography>
                                                    )}
                                                </Box>
                                            }
                                        />
                                    </ListItem>
                                    <Divider component="li" sx={{ borderColor: '#1a1a1a' }} />
                                </div>
                            ))}
                        </List>
                    </Paper>
                </Grid>

            </Grid>
        </Box>
        
        <ChannelManager 
            open={isManagerOpen} 
            onClose={() => setIsManagerOpen(false)} 
            channels={channels}
            onSave={handleSaveChannel}
            onDelete={handleDeleteChannel}
        />

        <Snackbar 
            open={!!errorMsg} 
            autoHideDuration={6000} 
            onClose={() => setErrorMsg(null)}
            anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
        >
            <Alert onClose={() => setErrorMsg(null)} severity="error" variant="filled" sx={{ width: '100%' }}>
                {errorMsg}
            </Alert>
        </Snackbar>
      </Box>
    </ThemeProvider>
  );
}

export default App;