import { useEffect, useState, useRef } from 'react';
import { AppBar, Toolbar, Typography, CssBaseline, ThemeProvider, createTheme, Box, Card, CardActionArea, Grid, List, ListItem, ListItemText, Divider, Paper, Chip } from '@mui/material';
import ScannerDisplay from './components/ScannerDisplay';
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import PauseIcon from '@mui/icons-material/Pause';
import HistoryIcon from '@mui/icons-material/History';
import AssessmentIcon from '@mui/icons-material/Assessment';
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
  const [scannerState, setScannerState] = useState<ScannerState>({ status: 'IDLE', signalStrength: 0 });
  const [channels, setChannels] = useState<Channel[]>([]);
  const [callLog, setCallLog] = useState<CallLog[]>([]);
  const [manualHold, setManualHold] = useState<number | null>(null);
  const ws = useRef<WebSocket | null>(null);

  // Helper to send commands
  const sendCommand = (action: string, frequency?: number) => {
    const backendHost = window.location.hostname;
    const httpUrl = `http://${backendHost}:3001/api/control`;
    
    fetch(httpUrl, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ action, frequency })
    }).catch(err => console.error("Command failed:", err));
  };

  const handleChannelClick = (ch: Channel) => {
      if (manualHold === ch.frequency) {
          setManualHold(null);
          sendCommand('scan');
      } else {
          setManualHold(ch.frequency);
          sendCommand('hold', ch.frequency);
      }
  };

  useEffect(() => {
    const backendHost = window.location.hostname;
    const httpUrl = `http://${backendHost}:3001/api/channels`;
    const wsUrl = `ws://${backendHost}:3001`;

    fetch(httpUrl)
      .then(res => res.json())
      .then(data => setChannels(data))
      .catch(err => console.error("Failed to fetch channels:", err));

    ws.current = new WebSocket(wsUrl);

    ws.current.onmessage = async (event) => {
      if (event.data instanceof Blob) {
        // Handle Audio
        const arrayBuffer = await event.data.arrayBuffer();
        const int16Array = new Int16Array(arrayBuffer);
        const float32Array = new Float32Array(int16Array.length);
        for (let i = 0; i < int16Array.length; i++) {
            float32Array[i] = int16Array[i] / 32768;
        }
        if (!window.audioCtx) {
            window.audioCtx = new (window.AudioContext || (window as any).webkitAudioContext)({ sampleRate: 8000 });
        }
        const ctx = window.audioCtx;
        const audioBuffer = ctx.createBuffer(1, float32Array.length, 8000);
        audioBuffer.copyToChannel(float32Array, 0);
        const source = ctx.createBufferSource();
        source.buffer = audioBuffer;
        source.connect(ctx.destination);
        if (!window.nextStartTime) window.nextStartTime = ctx.currentTime;
        if (window.nextStartTime < ctx.currentTime) window.nextStartTime = ctx.currentTime;
        source.start(window.nextStartTime);
        window.nextStartTime += audioBuffer.duration;
      } else {
        // Handle JSON
        try {
            const message = JSON.parse(event.data);
            if (message.type === 'STATE_UPDATE') {
              const newState = message.payload as ScannerState;
              setScannerState(prev => {
                  if (newState.status === 'RECEIVING' && prev.status !== 'RECEIVING' && newState.currentChannel) {
                      setCallLog(log => [{
                          id: Date.now(),
                          timestamp: new Date().toLocaleTimeString(),
                          channel: newState.currentChannel!
                      }, ...log].slice(0, 50));
                  }
                  return newState;
              });
            }
        } catch (e) {
            console.warn('Unknown message:', event.data);
        }
      }
    };

    return () => {
      if (ws.current) ws.current.close();
    };
  }, []);

  return (
    <ThemeProvider theme={darkTheme}>
      <CssBaseline />
      <Box sx={{ display: 'flex', flexDirection: 'column', height: '100vh', overflow: 'hidden' }}>
        
        {/* Header */}
        <AppBar position="static" elevation={0}>
            <Toolbar variant="dense">
                <Typography variant="h6" component="div" sx={{ flexGrow: 1, color: '#00ff00', fontWeight: '900', letterSpacing: 3 }}>
                    OPENSCANNER <span style={{fontSize: '0.7em', color: '#666'}}>P25</span>
                </Typography>
                {manualHold && (
                    <Chip label="MANUAL HOLD" color="warning" size="small" icon={<PauseIcon />} />
                )}
            </Toolbar>
        </AppBar>

        {/* Main Content Dashboard */}
        <Box sx={{ flexGrow: 1, p: 3, overflow: 'hidden' }}>
            <Grid container spacing={3} sx={{ height: '100%' }}>
                
                {/* Left Column: Active Scanner & Channel Grid */}
                <Grid item xs={12} md={8} lg={9} sx={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
                    
                    {/* Hero Widget */}
                    <Box sx={{ mb: 3 }}>
                        <ScannerDisplay state={scannerState} />
                    </Box>

                    {/* Channel Grid */}
                    <Paper sx={{ flexGrow: 1, p: 2, bgcolor: '#0a0a0a', border: '1px solid #222', borderRadius: 2, overflowY: 'auto' }}>
                        <Box display="flex" alignItems="center" mb={2} gap={1}>
                            <AssessmentIcon color="primary" fontSize="small" />
                            <Typography variant="subtitle2" color="text.secondary" fontWeight="bold">CHANNEL CONTROL</Typography>
                        </Box>
                        <Grid container spacing={2}>
                            {channels.map((ch) => (
                                <Grid item xs={12} sm={6} lg={4} key={ch.frequency}>
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

                {/* Right Column: Transmission Log */}
                <Grid item xs={12} md={4} lg={3} sx={{ height: '100%' }}>
                    <Paper sx={{ height: '100%', display: 'flex', flexDirection: 'column', bgcolor: '#0a0a0a', border: '1px solid #222', borderRadius: 2 }}>
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
                                    <ListItem alignItems="flex-start" sx={{ px: 2, py: 1 }}>
                                        <ListItemText 
                                            primary={
                                                <Typography variant="body2" fontWeight="bold" color="white">
                                                    {log.channel.alphaTag}
                                                </Typography>
                                            }
                                            secondary={
                                                <Typography variant="caption" color="gray" sx={{ display: 'flex', justifyContent: 'space-between' }}>
                                                    <span>{log.channel.frequency} MHz</span>
                                                    <span>{log.timestamp}</span>
                                                </Typography>
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
      </Box>
    </ThemeProvider>
  );
}

export default App;