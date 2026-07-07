import { useEffect, useState, useRef, useCallback } from 'react';
import { AppBar, Toolbar, Typography, CssBaseline, ThemeProvider, createTheme, Box, Card, Grid, Paper, Chip, IconButton, Snackbar, Alert, Tooltip, Slider, Button, Dialog, DialogTitle, DialogContent, DialogActions, TextField } from '@mui/material';
import ScannerDisplay from './components/ScannerDisplay';
import RfWaterfallDebug from './components/RfWaterfallDebug';
import ChannelManager from './components/ChannelManager';
import FireToneManager from './components/FireToneManager';
import SettingsManager from './components/SettingsManager';
import TransmissionLog from './components/TransmissionLog';
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import PauseIcon from '@mui/icons-material/Pause';
import AssessmentIcon from '@mui/icons-material/Assessment';
import LocationOnIcon from '@mui/icons-material/LocationOn';
import SpeedIcon from '@mui/icons-material/Speed';
import SatelliteAltIcon from '@mui/icons-material/SatelliteAlt';
import AccessTimeIcon from '@mui/icons-material/AccessTime';
import UsbIcon from '@mui/icons-material/Usb';
import UsbOffIcon from '@mui/icons-material/UsbOff';
import EditIcon from '@mui/icons-material/Edit';
import NotificationsActiveIcon from '@mui/icons-material/NotificationsActive';
import FullscreenIcon from '@mui/icons-material/Fullscreen';
import FullscreenExitIcon from '@mui/icons-material/FullscreenExit';
import SupportAgentIcon from '@mui/icons-material/SupportAgent';
import SettingsIcon from '@mui/icons-material/Settings';
import VolumeUpIcon from '@mui/icons-material/VolumeUp';
import MonitorIcon from '@mui/icons-material/Monitor';
import type { ScannerState, Channel, CallLog, FireToneSet, RadioEvent } from './types';
import EventLog from './components/EventLog';

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
  const [isAudioInitialized, setIsAudioInitialized] = useState(false);
  const [currentTime, setCurrentTime] = useState(new Date());
  const [scannerState, setScannerState] = useState<ScannerState>({ status: 'IDLE', signalStrength: 0 });
  const [channels, setChannels] = useState<Channel[]>([]);
  const [fireTones, setFireTones] = useState<FireToneSet[]>([]);
  const [callLog, setCallLog] = useState<CallLog[]>([]);
  const [radioEvents, setRadioEvents] = useState<RadioEvent[]>([]);
  const [highlightLog, setHighlightLog] = useState<{ id: string; seq: number } | null>(null);
  const [audioAnalyser, setAudioAnalyser] = useState<AnalyserNode | undefined>(undefined);
  const [isManagerOpen, setIsManagerOpen] = useState(false);
  const [isToneManagerOpen, setIsToneManagerOpen] = useState(false);
  const [isSettingsOpen, setIsSettingsOpen] = useState(false);
  const [isDebugModalOpen, setIsDebugModalOpen] = useState(false);
  const [debugFreq, setDebugFreq] = useState<string>('155.500');
  const [debugGain, setDebugGain] = useState<number>(40);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);
  const [isConnected, setIsConnected] = useState(false);
  const [playingId, setPlayingId] = useState<string | null>(null);
  const [isFullscreen, setIsFullscreen] = useState(false);
  const [volume, setVolume] = useState<number>(() => {
    const saved = localStorage.getItem('scannerVolume');
    return saved !== null ? parseFloat(saved) : 1.0;
  });
  const wsControl = useRef<WebSocket | null>(null);
  const wsAudio = useRef<WebSocket | null>(null);
  const audioAnalyserRef = useRef<AnalyserNode | null>(null);
  const gainNodeRef = useRef<GainNode | null>(null);
  const filterNodeRef = useRef<BiquadFilterNode | null>(null);
  const wakeLock = useRef<WakeLockSentinel | null>(null);
  const activeSource = useRef<AudioBufferSourceNode | null>(null);
  const isParallelRef = useRef(false);
  const isPageHiddenRef = useRef(false);
  const volumeRef = useRef(volume);

  const manualHold = scannerState.manualHoldFrequency;

  // Update volume when state changes
  useEffect(() => {
    volumeRef.current = volume;
    if (gainNodeRef.current) {
      gainNodeRef.current.gain.setTargetAtTime(volume, window.audioCtx?.currentTime || 0, 0.05);
    }
    localStorage.setItem('scannerVolume', volume.toString());
  }, [volume]);

  // Initialize Audio Context on Interaction
  const initAudio = useCallback(async () => {
      if (!window.audioCtx) {
          const AudioContextClass = window.AudioContext || window.webkitAudioContext;
          if (AudioContextClass) {
              window.audioCtx = new AudioContextClass({ sampleRate: 48000 });
          }
      }

      if (window.audioCtx && !gainNodeRef.current) {
          const gainNode = window.audioCtx.createGain();
          gainNode.gain.value = volume;
          gainNode.connect(window.audioCtx.destination);
          gainNodeRef.current = gainNode;
      }

      if (window.audioCtx && !filterNodeRef.current) {
          const filter = window.audioCtx.createBiquadFilter();
          filter.type = 'lowpass';
          filter.frequency.value = 2000;
          if (gainNodeRef.current) {
              filter.connect(gainNodeRef.current);
          }
          filterNodeRef.current = filter;
      }

      if (window.audioCtx && window.audioCtx.state === 'suspended') {
          await window.audioCtx.resume();
      }
      setIsAudioInitialized(true);
  }, [volume]);

  useEffect(() => {
      window.addEventListener('click', initAudio);
      window.addEventListener('touchstart', initAudio);
      return () => {
          window.removeEventListener('click', initAudio);
          window.removeEventListener('touchstart', initAudio);
      };
  }, [initAudio]);

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
      document.exitFullscreen().catch(err => {
        console.error(`Error attempting to exit full-screen mode: ${err.message} (${err.name})`);
      });
    }
  };

  const downloadSupportPackage = () => {
    const url = `/api/support/package`;
    
    // Create a temporary link to trigger download with a better UX than window.location.href
    const link = document.createElement('a');
    link.href = url;
    link.setAttribute('download', ''); // Request download
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
  };

  const handleOpenDebug = () => {
    setIsDebugModalOpen(true);
    sendCommand('debug_spectrum', Number(debugFreq), debugGain);
  };

  // Wake Lock Manager
  useEffect(() => {
    const requestWakeLock = async () => {
      if ('wakeLock' in navigator && scannerState.status !== 'IDLE' && navigator.wakeLock) {
        try {
          if (!wakeLock.current) {
            wakeLock.current = await navigator.wakeLock.request('screen');
            console.log('Wake Lock active');
            wakeLock.current.addEventListener('release', () => {
               wakeLock.current = null;
               console.log('Wake Lock released');
            });
          }
        } catch (err: unknown) {
          if (err instanceof Error) {
            console.error(`${err.name}, ${err.message}`);
          }
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
      if (wakeLock.current) {
        wakeLock.current.release().catch(err => {
          console.warn('Failed to release wake lock:', err);
        });
      }
    };
  }, [scannerState.status]);

  // Track page visibility to handle background tab audio issues
  useEffect(() => {
    const onVisibilityChange = () => {
      isPageHiddenRef.current = document.visibilityState === 'hidden';
      if (document.visibilityState === 'visible' && nextStartTime.current > 0) {
        nextStartTime.current = 0;
      }
    };
    document.addEventListener('visibilitychange', onVisibilityChange);
    return () => document.removeEventListener('visibilitychange', onVisibilityChange);
  }, []);

  // Clock Effect
  useEffect(() => {
    const timer = setInterval(() => setCurrentTime(new Date()), 1000);
    return () => clearInterval(timer);
  }, []);

  // Low-level REST helper for scanner control endpoints
  const scannerApi = (path: string, method: string, body?: object) =>
    fetch(`/api/scanner/${path}`, {
        method,
        headers: body ? { 'Content-Type': 'application/json' } : undefined,
        body: body ? JSON.stringify(body) : undefined,
    }).catch(err => console.error("Command failed:", err));

  // Helper to send commands, mapping legacy action names onto REST endpoints
  const sendCommand = (action: string, frequency?: number, value?: number) => {
    switch (action) {
        case 'scan': return scannerApi('hold', 'DELETE');
        case 'hold': return scannerApi('hold', 'PUT', { frequency });
        case 'start': return scannerApi('power', 'PUT', { enabled: true });
        case 'stop': return scannerApi('power', 'PUT', { enabled: false });
        case 'set_squelch': return scannerApi('squelch', 'PUT', { value });
        case 'debug_spectrum': return scannerApi('debug-spectrum', 'POST', { frequency, gain: value });
        default: console.error("Unknown command:", action);
    }
  };

  const handleSkip = (freq?: number) => {
    if (freq) {
        scannerApi('avoids', 'POST', { frequency: freq, duration: 10 });
    } else {
        sendCommand('scan');
    }
  };

  const handleChannelClick = async (ch: Channel) => {
      // Resume audio context on user interaction
      if (window.audioCtx && window.audioCtx.state === 'suspended') {
          window.audioCtx.resume();
      }

      // Check if we are already holding this frequency (with tolerance for float precision)
      const isHolding = manualHold !== undefined && Math.abs(manualHold - ch.frequency) < 0.0001;

      if (isHolding) {
          sendCommand('scan');
      } else {
          // If the channel is avoided, un-avoid it first
          if (ch.avoid) {
              await handleSaveChannel({ ...ch, avoid: false });
          }
          sendCommand('hold', ch.frequency);
      }
  };

  const nextStartTime = useRef<number>(0);

  // Audio Playback Helper for recorded files
  const playRawAudio = async (id: string, filename: string, duration?: number) => {
    let ctx = window.audioCtx;
    if (!ctx) {
        const AudioContextClass = window.AudioContext || window.webkitAudioContext;
        if (AudioContextClass) {
            ctx = new AudioContextClass({ sampleRate: 48000 });
            window.audioCtx = ctx;
        } else {
            return;
        }
    }
    
    if (ctx.state === 'suspended') {
        await ctx.resume();
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
        
        let buffer: AudioBuffer;

        if (filename.endsWith('.raw')) {
            const int16Array = new Int16Array(arrayBuffer);
            console.log(`Playing raw audio: ${filename}, size: ${int16Array.length} samples`);
            
            const float32Array = new Float32Array(int16Array.length);
            for (let i = 0; i < int16Array.length; i++) {
                float32Array[i] = int16Array[i] / 32768;
            }

            // Auto-detect sample rate based on duration
            let sampleRate = 48000;
            if (duration && duration > 0) {
                const calculatedRate = int16Array.length / duration;
                if (Math.abs(calculatedRate - 8000) < Math.abs(calculatedRate - 48000)) {
                    sampleRate = 8000;
                }
            }
            console.log(`Detected Sample Rate: ${sampleRate}Hz`);

            buffer = ctx.createBuffer(1, float32Array.length, sampleRate);
            buffer.copyToChannel(float32Array, 0);
        } else {
            // MP3/WAV - Use browser decoder
            console.log(`Playing compressed audio: ${filename}`);
            buffer = await ctx.decodeAudioData(arrayBuffer);
        }

        const source = ctx.createBufferSource();
        source.buffer = buffer;
        
        if (!gainNodeRef.current) {
            const gainNode = ctx.createGain();
            gainNode.gain.value = volume;
            gainNode.connect(ctx.destination);
            gainNodeRef.current = gainNode;
        }
        source.connect(gainNodeRef.current);
        
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
    try {
        const response = await fetch(`/api/history/${id}`, { method: 'DELETE' });
        if (response.ok) {
            setCallLog(prev => prev.filter(log => log.id !== id));
        } else {
            console.error("Delete failed with status:", response.status);
        }
    } catch (e) {
        console.error("Delete failed:", e);
    }
  };

  const refreshChannels = () => {
    fetch(`/api/channels`)
      .then(res => res.json())
      .then(data => setChannels(data))
      .catch(err => console.error("Failed to fetch channels:", err));
  };

  const refreshFireTones = () => {
    fetch(`/api/firetones`)
      .then(res => res.json())
      .then(data => setFireTones(data))
      .catch(err => console.error("Failed to fetch fire tones:", err));
  };

  const clearEvents = async () => {
    try {
      await fetch(`/api/events`, { method: 'DELETE' });
      setRadioEvents([]);
    } catch (err) {
      console.error("Failed to clear events:", err);
    }
  };

  const handleSaveChannel = async (channel: Channel) => {
    const baseUrl = `/api/channels`;

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
    try {
        await fetch(`/api/channels/${id}`, { method: 'DELETE' });
        refreshChannels();
    } catch (e) {
        console.error("Delete channel failed:", e);
    }
  };

  const handleSaveFireTone = async (tone: FireToneSet) => {
    const baseUrl = `/api/firetones`;

    const method = tone.id ? 'PUT' : 'POST';
    const url = tone.id ? `${baseUrl}/${tone.id}` : baseUrl;

    try {
        await fetch(url, {
            method,
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(tone)
        });
        refreshFireTones();
    } catch (e) {
        console.error("Save fire tone failed:", e);
    }
  };

  const handleDeleteFireTone = async (id: number) => {
    try {
        await fetch(`/api/firetones/${id}`, { method: 'DELETE' });
        refreshFireTones();
    } catch (e) {
        console.error("Delete fire tone failed:", e);
    }
  };

  useEffect(() => {
    const channelsUrl = `/api/channels`;
    const firetonesUrl = `/api/firetones`;
    const historyUrl = `/api/history`;
    
    const wsProtocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsControlUrl = `${wsProtocol}//${window.location.host}/ws/control`;
    const wsAudioUrl = `${wsProtocol}//${window.location.host}/ws/audio`;

    fetch(channelsUrl)
      .then(res => res.json())
      .then(data => setChannels(data))
      .catch(err => console.error("Failed to fetch channels:", err));

    fetch(firetonesUrl)
      .then(res => res.json())
      .then(data => setFireTones(data))
      .catch(err => console.error("Failed to fetch fire tones:", err));

    fetch(historyUrl)
      .then(res => res.json())
      .then(data => setCallLog(data))
      .catch(err => console.error("Failed to fetch history:", err));

    fetch(`/api/events`)
      .then(res => res.json())
      .then(data => setRadioEvents(data))
      .catch(err => console.error("Failed to fetch events:", err));

    const connectControlWs = () => {
        wsControl.current = new WebSocket(wsControlUrl);
        
        wsControl.current.onopen = () => {
            setIsConnected(true);
            console.log("Control WebSocket Connected");
        };

        wsControl.current.onclose = () => {
            setIsConnected(false);
            console.log("Control WebSocket Disconnected, retrying in 3s...");
            setTimeout(connectControlWs, 3000);
        };

        wsControl.current.onmessage = async (event) => {
            // Handle JSON
            try {
                const message = JSON.parse(event.data);
                if (message.type === 'STATE_UPDATE') {
                  const newState = message.payload as ScannerState;
                  isParallelRef.current = !!(newState.parallelChannels && newState.parallelChannels.length > 0);
                  setScannerState(newState);
                } else if (message.type === 'NEW_LOG') {
                  const newEntry = message.payload as CallLog;
                  setCallLog(log => {
                    const exists = log.some(x => x.id === newEntry.id);
                    if (exists) {
                      return log.map(x => x.id === newEntry.id ? newEntry : x);
                    } else {
                      return [newEntry, ...log].slice(0, 100);
                    }
                  });
                } else if (message.type === 'NEW_EVENT') {
                  const newEvent = message.payload as RadioEvent;
                  setRadioEvents(events => {
                    if (events.some(x => x.id === newEvent.id)) return events;
                    return [newEvent, ...events].slice(0, 100);
                  });
                } else if (message.type === 'ERROR') {
                  setErrorMsg(message.payload);
                }
            } catch (err) {
                console.warn('Unknown control message or parse error:', event.data, err);
            }
        };
    };

    const connectAudioWs = () => {
        wsAudio.current = new WebSocket(wsAudioUrl);

        wsAudio.current.onopen = () => {
             console.log("Audio WebSocket Connected");
        };

        wsAudio.current.onclose = () => {
             console.log("Audio WebSocket Disconnected, retrying in 3s...");
             setTimeout(connectAudioWs, 3000);
        };

        wsAudio.current.onmessage = async (event) => {
          // Debug: Log EVERYTHING received
          // console.log("[Audio DEBUG] Message received:", event.data);

          if (event.data instanceof Blob) {
            // Skip audio processing when tab is hidden to prevent scheduler catch-up bursts
            if (isPageHiddenRef.current) return;

            try {
                // Log incoming data size
                // console.log(`[Audio] Received Blob size: ${event.data.size}`);

                // Handle Audio
                const arrayBuffer = await event.data.arrayBuffer();
                
                // Validate if it makes sense as Int16 (Raw PCM)
                if (arrayBuffer.byteLength % 2 !== 0) {
                    console.warn(`[Audio] Warning: Byte length ${arrayBuffer.byteLength} is not a multiple of 2 (Int16)`);
                }

                const int16Array = new Int16Array(arrayBuffer);

                // Determine mono vs stereo based on parallel scan mode
                const isStereo = isParallelRef.current;
                
                let ctx = window.audioCtx;
                if (!ctx) {
                    console.log("[Audio] Initializing new AudioContext...");
                    const AudioContextClass = window.AudioContext || window.webkitAudioContext;
                    if (AudioContextClass) {
                        ctx = new AudioContextClass({ sampleRate: 48000 });
                        window.audioCtx = ctx;
                    }
                }

                if (ctx && ctx.state === 'suspended') {
                    console.log("[Audio] Resuming suspended context...");
                    await ctx.resume();
                }

                // Reset scheduler after context resume if it drifted far ahead (>=1s)
                if (ctx && nextStartTime.current > ctx.currentTime + 1.0) {
                    nextStartTime.current = 0;
                }

                if (!ctx) {
                    console.error("[Audio] Failed to get AudioContext");
                    return;
                }

                let audioBuffer: AudioBuffer;
                if (isStereo && int16Array.length >= 2) {
                    // Stereo: interleaved L,R,L,R samples from parallel scan
                    const frameSamples = Math.floor(int16Array.length / 2);
                    const leftChannel = new Float32Array(frameSamples);
                    const rightChannel = new Float32Array(frameSamples);
                    for (let i = 0; i < frameSamples; i++) {
                        leftChannel[i] = int16Array[i * 2] / 32768;
                        rightChannel[i] = int16Array[i * 2 + 1] / 32768;
                    }
                    audioBuffer = ctx.createBuffer(2, frameSamples, 48000);
                    audioBuffer.copyToChannel(leftChannel, 0);
                    audioBuffer.copyToChannel(rightChannel, 1);
                } else {
                    // Mono: single channel
                    const float32Array = new Float32Array(int16Array.length);
                    for (let i = 0; i < int16Array.length; i++) {
                        float32Array[i] = int16Array[i] / 32768;
                    }
                    audioBuffer = ctx.createBuffer(1, float32Array.length, 48000);
                    audioBuffer.copyToChannel(float32Array, 0);
                }

                // Ensure gain node belongs to current context
                if (!gainNodeRef.current || gainNodeRef.current.context !== ctx) {
                    console.log("[Audio] Recreating GainNode for current context");
                    const gainNode = ctx.createGain();
                    gainNode.gain.value = volumeRef.current;
                    gainNode.connect(ctx.destination);
                    gainNodeRef.current = gainNode;
                    filterNodeRef.current = null; // Force filter recreation
                }

                if (!filterNodeRef.current || filterNodeRef.current.context !== ctx) {
                    const filter = ctx.createBiquadFilter();
                    filter.type = 'lowpass';
                    filter.frequency.value = 2000;
                    filter.connect(gainNodeRef.current);
                    filterNodeRef.current = filter;
                }

                let analyser = audioAnalyserRef.current;
                
                // If analyser is missing or belongs to a different/closed context, recreate it
                if (!analyser || analyser.context !== ctx || analyser.context.state === 'closed') {
                    console.log("[Audio] Recreating AnalyserNode");
                    analyser = ctx.createAnalyser();
                    analyser.fftSize = 1024;
                    
                    analyser.connect(filterNodeRef.current);
                    
                    audioAnalyserRef.current = analyser;
                    setAudioAnalyser(analyser);
                }

                const source = ctx.createBufferSource();
                source.buffer = audioBuffer;
                
                // Safe connection
                if (analyser) {
                    source.connect(analyser);
                    
                    // Scheduler to prevent crackling/overlaps
                    const currentTime = ctx.currentTime;
                    // Jitter buffer: 0.15s (150ms) gives more headroom than 50ms to prevent choppy audio
                    // caused by network variance, at the cost of slight latency.
                    const JITTER_BUFFER = 0.15; 
                    const MAX_DRIFT = 0.5; // Reset if > 500ms ahead

                    if (nextStartTime.current < currentTime) {
                        // Underrun: We fell behind. Resume immediately + small safety buffer
                        // console.log("[Audio] Underrun detected, resetting sync");
                        nextStartTime.current = currentTime + 0.05; 
                    } else if (nextStartTime.current > currentTime + MAX_DRIFT) {
                        // Drift: We are too far ahead. Reset to tight buffer.
                        // console.log("[Audio] Large drift detected, resetting sync");
                        nextStartTime.current = currentTime + JITTER_BUFFER;
                    }
                    
                    source.start(nextStartTime.current);
                    nextStartTime.current += audioBuffer.duration;
                } else {
                    console.error("[Audio] AnalyserNode is invalid, dropping audio packet");
                }
            } catch (err) {
                console.error("[Audio] Processing error:", err);
            }
          }
        };
    };

    connectControlWs();
    connectAudioWs();

    const resumeAudio = () => {
        if (window.audioCtx && window.audioCtx.state === 'suspended') {
            window.audioCtx.resume();
        }
    };
    window.addEventListener('click', resumeAudio);

    return () => {
      window.removeEventListener('click', resumeAudio);
      if (wsControl.current) wsControl.current.close();
      if (wsAudio.current) wsAudio.current.close();
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
                <Typography variant="h6" component="div" sx={{ flexGrow: 1, color: '#00ff00', fontWeight: '900', letterSpacing: { xs: 1, sm: 3 }, fontSize: { xs: '1.1rem', sm: '1.25rem' } }}>
                    OPENSCANNER <span style={{fontSize: '0.7em', color: '#666'}}>P25</span>
                </Typography>

                <Box sx={{ mr: 2, display: { xs: 'none', sm: 'block' } }}>
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

                <Box sx={{ mr: { xs: 1, sm: 3 }, px: { xs: 1, sm: 2 }, py: 0.5, bgcolor: '#111', borderRadius: 1, border: '1px solid #333', display: 'flex', alignItems: 'center', gap: 1 }}>
                    {scannerState.gps?.time && scannerState.gps.fix >= 2 
                        ? <SatelliteAltIcon sx={{ fontSize: 16, color: 'primary.main' }} />
                        : <AccessTimeIcon sx={{ fontSize: 16, color: 'text.secondary' }} />
                    }
                    <Typography variant="caption" sx={{ fontFamily: 'monospace', color: 'primary.main', fontWeight: 'bold', fontSize: { xs: '12px', sm: '14px' } }}>
                        {scannerState.gps?.time && scannerState.gps.fix >= 2 
                            ? new Date(scannerState.gps.time).toLocaleTimeString([], { hour12: false })
                            : currentTime.toLocaleTimeString([], { hour12: false })
                        }
                    </Typography>
                </Box>
                
                {scannerState.gps && (
                    <Box sx={{ display: { xs: 'none', md: 'flex' }, alignItems: 'center', gap: 2, mr: 3, px: 2, py: 0.5, bgcolor: '#111', borderRadius: 1, border: '1px solid #333' }}>
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
                        <Chip label="HOLD" color="warning" size="small" icon={<PauseIcon />} sx={{ display: { xs: 'none', sm: 'flex' } }} />
                        <Chip 
                            label="RESUME" 
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

                <Box sx={{ ml: 2, display: 'flex', alignItems: 'center', gap: 2, width: { xs: '80px', sm: '120px', md: '150px' } }}>
                    <VolumeUpIcon sx={{ color: 'text.secondary', fontSize: 20 }} />
                    <Slider
                        size="small"
                        value={volume}
                        min={0}
                        max={1}
                        step={0.01}
                        onChange={(_, value) => setVolume(value as number)}
                        aria-label="Volume"
                        sx={{
                            color: 'primary.main',
                            '& .MuiSlider-thumb': {
                                width: 12,
                                height: 12,
                                '&:before': {
                                    boxShadow: '0 2px 12px 0 rgba(0,0,0,0.4)',
                                },
                            },
                            '& .MuiSlider-rail': {
                                opacity: 0.3,
                            },
                        }}
                    />
                </Box>

                <Tooltip title="Fire Tone Outs">
                    <IconButton color="inherit" onClick={() => setIsToneManagerOpen(true)} sx={{ ml: 1, display: { xs: 'none', sm: 'inline-flex' } }}>
                        <NotificationsActiveIcon />
                    </IconButton>
                </Tooltip>

                <Tooltip title="Settings">
                    <IconButton color="inherit" onClick={() => setIsSettingsOpen(true)} sx={{ ml: 1, display: { xs: 'none', sm: 'inline-flex' } }}>
                        <SettingsIcon />
                    </IconButton>
                </Tooltip>

                <Tooltip title={isFullscreen ? "Exit Fullscreen" : "Fullscreen"}>
                    <IconButton color="inherit" onClick={toggleFullscreen} sx={{ ml: 1, display: { xs: 'none', sm: 'inline-flex' } }}>
                        {isFullscreen ? <FullscreenExitIcon /> : <FullscreenIcon />}
                    </IconButton>
                </Tooltip>

                <Tooltip title="RF Spectrum Debug">
                    <IconButton color="inherit" onClick={handleOpenDebug} sx={{ ml: 1, display: { xs: 'none', sm: 'inline-flex' } }}>
                        <MonitorIcon />
                    </IconButton>
                </Tooltip>

                <Tooltip title="Download Support Package">
                    <IconButton color="inherit" onClick={downloadSupportPackage} sx={{ ml: 1, display: { xs: 'none', sm: 'inline-flex' } }}>
                        <SupportAgentIcon />
                    </IconButton>
                </Tooltip>
            </Toolbar>
        </AppBar>

        {/* Main Content Dashboard */}
        <Box sx={{ flexGrow: 1, p: { xs: 1, sm: 2 }, height: '100%', overflowY: { xs: 'auto', md: 'hidden' } }}>
            <Grid container spacing={2} sx={{ height: { xs: 'auto', md: '100%' } }}>
                
                {/* Left Column: Active Scanner & Channel Grid (Compact) */}
                <Grid size={{ xs: 12, md: 4, lg: 3 }} sx={{ display: 'flex', flexDirection: 'column', height: { xs: 'auto', md: '100%' }, overflow: { xs: 'visible', md: 'hidden' } }}>
                    
                    {/* Hero Widget */}
                    <Box sx={{ mb: 2 }}>
                        <ScannerDisplay 
                            state={scannerState} 
                            analyser={audioAnalyser}
                            channels={channels}
                            onScan={handleSkip}
                        />
                    </Box>

                    {/* Channel Grid */}
                    <Paper sx={{ flexGrow: 1, p: 2, bgcolor: '#0a0a0a', border: '1px solid #222', borderRadius: 2, overflowY: 'auto', minHeight: { xs: '300px', md: 0 } }}>
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
                                <Grid size={{ xs: 12, sm: 6, md: 12 }} key={ch.frequency}>
                                    <Card 
                                        sx={{ 
                                            border: manualHold === ch.frequency ? '1px solid #ff9800' : '1px solid #333',
                                        bgcolor: '#151515',
                                            transition: 'all 0.2s',
                                            '&:hover': { bgcolor: '#222' }
                                        }}
                                    >
                                        <Box sx={{ p: 2 }}>
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
                                                <Chip 
                                                    label={['FM', 'AM', 'WFM'].includes(ch.mode?.toUpperCase()) ? `${ch.mode} (EXP)` : ch.mode} 
                                                    size="small" 
                                                    sx={{ height: 20, fontSize: '0.65rem', bgcolor: '#333' }} 
                                                />
                                                <Box flexGrow={1} />
                                                <Button
                                                    variant="contained"
                                                    size="small"
                                                    onClick={async () => {
                                                        const newAvoid = !ch.avoid;
                                                        await handleSaveChannel({ ...ch, avoid: newAvoid });
                                                        // If we are now avoiding the channel we are holding, stop holding
                                                        if (newAvoid && manualHold !== undefined && Math.abs(manualHold - ch.frequency) < 0.0001) {
                                                            sendCommand('scan');
                                                        }
                                                    }}
                                                    sx={{ 
                                                        bgcolor: ch.avoid ? 'error.main' : '#1c1c1c',
                                                        color: ch.avoid ? 'white' : 'text.primary',
                                                        minWidth: 'auto', 
                                                        padding: '4px 8px', 
                                                        fontSize: '0.7rem',
                                                        mr: 1
                                                    }}
                                                >
                                                    AVOID
                                                </Button>
                                                <Button
                                                    variant="contained"
                                                    size="small"
                                                    onClick={() => handleChannelClick(ch)}
                                                    sx={{ 
                                                        bgcolor: manualHold === ch.frequency ? 'warning.main' : '#1c1c1c',
                                                        color: manualHold === ch.frequency ? 'white' : 'text.primary',
                                                        minWidth: 'auto', 
                                                        padding: '4px 8px', 
                                                        fontSize: '0.7rem'
                                                    }}
                                                >
                                                    HOLD
                                                </Button>
                                            </Box>
                                        </Box>
                                    </Card>
                                </Grid>
                            ))}
                        </Grid>
                    </Paper>
                </Grid>

                {/* Right Column: Transmission Log (Expanded) */}
                <Grid size={{ xs: 12, md: 8, lg: 9 }} sx={{ height: { xs: '500px', md: '100%' }, overflow: 'hidden' }}>
                    <Paper sx={{ height: '100%', display: 'flex', flexDirection: 'column', bgcolor: '#0a0a0a', border: '1px solid #222', borderRadius: 2, overflowY: 'auto' }}>
                        {fireTones.length > 0 && (
                            <EventLog
                                events={radioEvents}
                                onClear={clearEvents}
                                onEventClick={(e) => {
                                    if (e.transmissionId) {
                                        setHighlightLog(h => ({ id: e.transmissionId!, seq: (h?.seq ?? 0) + 1 }));
                                    }
                                }}
                            />
                        )}
                        <TransmissionLog
                            liveLogs={callLog}
                            playingId={playingId}
                            onPlay={playRawAudio}
                            onDelete={deleteEntry}
                            highlight={highlightLog}
                        />
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

        <FireToneManager 
            open={isToneManagerOpen} 
            onClose={() => setIsToneManagerOpen(false)} 
            tones={fireTones}
            onSave={handleSaveFireTone}
            onDelete={handleDeleteFireTone}
        />

        <SettingsManager
            open={isSettingsOpen}
            onClose={() => setIsSettingsOpen(false)}
            onRecordingsDeleted={() => setCallLog([])}
        />

        <Dialog 
            open={isDebugModalOpen} 
            onClose={() => {
                if (scannerState.status === 'DEBUG') {
                    sendCommand('scan');
                }
                setIsDebugModalOpen(false);
            }}
            maxWidth="lg"
            fullWidth
            PaperProps={{
                sx: { bgcolor: '#050505', border: '1px solid #333' }
            }}
        >
            <DialogTitle sx={{ borderBottom: '1px solid #222', p: 3 }}>
                <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                    <Typography variant="h5" sx={{ color: 'primary.main', fontWeight: 'bold', letterSpacing: 1 }}>RF SPECTRUM DEBUG</Typography>
                </Box>
            </DialogTitle>
            <DialogContent sx={{ p: 3 }}>
                <Box sx={{ mb: 4, mt: 1, display: 'flex', gap: 3, alignItems: 'center', flexWrap: 'wrap' }}>
                    <TextField 
                        label="Center Frequency"
                        variant="outlined"
                        size="small"
                        value={debugFreq}
                        onChange={(e) => setDebugFreq(e.target.value)}
                        sx={{ width: 180 }}
                    />

                    <Box sx={{ width: 180 }}>
                        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 0.5 }}>
                            SDR GAIN: {debugGain === 0 ? 'AUTO' : `${debugGain} dB`}
                        </Typography>
                        <Slider 
                            value={debugGain}
                            min={0}
                            max={50}
                            step={1}
                            onChange={(_, val) => setDebugGain(val as number)}
                            valueLabelDisplay="auto"
                            valueLabelFormat={(val) => val === 0 ? 'AUTO' : `${val}dB`}
                            size="small"
                        />
                    </Box>

                    <Button 
                        variant="contained" 
                        color="primary"
                        onClick={() => sendCommand('debug_spectrum', Number(debugFreq), debugGain)}
                        disabled={scannerState.status === 'DEBUG' && scannerState.currentFrequency === Number(debugFreq) && scannerState.gain === debugGain}
                        sx={{ height: 40, px: 4 }}
                    >
                        TUNE
                    </Button>
                    <Box flexGrow={1} />
                    <Paper variant="outlined" sx={{ px: 2, py: 1, bgcolor: 'rgba(255,255,255,0.03)', display: 'flex', alignItems: 'center', gap: 2 }}>
                        <Typography variant="caption" color="text.secondary">SDR STATUS:</Typography>
                        <Chip 
                            label={scannerState.status} 
                            size="small" 
                            color={scannerState.status === 'DEBUG' ? 'success' : 'default'} 
                            sx={{ fontWeight: 'bold', height: 20, fontSize: '10px' }} 
                        />
                    </Paper>
                </Box>
                
                <RfWaterfallDebug data={scannerState.rfSpectrum} height={500} />
                
                <Box sx={{ mt: 3, p: 2, bgcolor: 'rgba(255, 255, 255, 0.02)', borderRadius: 1, border: '1px solid #222' }}>
                    <Box sx={{ display: 'flex', gap: 1.5, alignItems: 'center', mb: 1 }}>
                        <Typography variant="caption" sx={{ color: '#ffaa00', fontWeight: 'bold', display: 'flex', alignItems: 'center', minWidth: '100px' }}>
                            SYSTEM NOTE
                        </Typography>
                        <Typography variant="caption" color="text.secondary">
                            Debug mode requires exclusive hardware access. Scanning and decoding are suspended while active.
                        </Typography>
                    </Box>
                    <Box sx={{ display: 'flex', gap: 1.5, alignItems: 'center' }}>
                        <Typography variant="caption" sx={{ color: '#00ccff', fontWeight: 'bold', display: 'flex', alignItems: 'center', minWidth: '100px' }}>
                            HARDWARE TIP
                        </Typography>
                        <Typography variant="caption" color="text.secondary">
                            The center spike is a DC offset common in RTL-SDR hardware and does not indicate a real signal.
                        </Typography>
                    </Box>
                </Box>
            </DialogContent>
            <DialogActions sx={{ borderTop: '1px solid #222', p: 2 }}>
                <Button onClick={() => {
                    if (scannerState.status === 'DEBUG') {
                        sendCommand('scan');
                    }
                    setIsDebugModalOpen(false);
                }} color="inherit">CLOSE</Button>
            </DialogActions>
        </Dialog>

        <Snackbar 
            open={!isAudioInitialized && (scannerState.status === 'RECEIVING' || scannerState.status === 'MONITORING')} 
            anchorOrigin={{ vertical: 'top', horizontal: 'center' }}
        >
            <Alert severity="info" variant="filled" sx={{ width: '100%', cursor: 'pointer' }} onClick={initAudio}>
                Click anywhere to enable live audio
            </Alert>
        </Snackbar>

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