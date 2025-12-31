import { useEffect, useState, useRef } from 'react';
import { Container, Stack, AppBar, Toolbar, Typography, CssBaseline, ThemeProvider, createTheme, Box } from '@mui/material';
import ScannerDisplay from './components/ScannerDisplay';
import type { ScannerState, Channel } from './types';

const darkTheme = createTheme({
  palette: {
    mode: 'dark',
    primary: {
      main: '#00ff00',
    },
    background: {
      default: '#0a0a0a',
      paper: '#1a1a1a',
    },
  },
  typography: {
    fontFamily: '"Roboto Mono", "Roboto", "Helvetica", "Arial", sans-serif',
  },
});

function App() {
  const [scannerState, setScannerState] = useState<ScannerState>({ status: 'IDLE', signalStrength: 0 });
  const [channels, setChannels] = useState<Channel[]>([]);
  const ws = useRef<WebSocket | null>(null);

  useEffect(() => {
    // Determine backend host (assumes backend runs on same host, port 3001)
    const backendHost = window.location.hostname;
    const httpUrl = `http://${backendHost}:3001/api/channels`;
    const wsUrl = `ws://${backendHost}:3001`;

    // Fetch channels
    fetch(httpUrl)
      .then(res => res.json())
      .then(data => setChannels(data))
      .catch(err => console.error("Failed to fetch channels:", err));

    // Connect WebSocket
    ws.current = new WebSocket(wsUrl);

    ws.current.onopen = () => {
      console.log('Connected to Scanner Server');
    };

    ws.current.onmessage = async (event) => {
      if (event.data instanceof Blob) {
        // Handle Audio Stream
        const arrayBuffer = await event.data.arrayBuffer();
        const int16Array = new Int16Array(arrayBuffer);
        const float32Array = new Float32Array(int16Array.length);
        
        // Convert Int16 PCM to Float32
        for (let i = 0; i < int16Array.length; i++) {
            float32Array[i] = int16Array[i] / 32768;
        }

        // Initialize AudioContext if needed (requires user interaction first usually)
        if (!window.audioCtx) {
            window.audioCtx = new (window.AudioContext || (window as any).webkitAudioContext)({ sampleRate: 8000 });
        }
        const ctx = window.audioCtx;

        // Create buffer
        const audioBuffer = ctx.createBuffer(1, float32Array.length, 8000);
        audioBuffer.copyToChannel(float32Array, 0);

        // Play
        const source = ctx.createBufferSource();
        source.buffer = audioBuffer;
        source.connect(ctx.destination);
        
        // Simple queueing to prevent overlap/gaps
        if (!window.nextStartTime) window.nextStartTime = ctx.currentTime;
        if (window.nextStartTime < ctx.currentTime) window.nextStartTime = ctx.currentTime;
        
        source.start(window.nextStartTime);
        window.nextStartTime += audioBuffer.duration;
        
      } else {
        // Handle JSON Control Messages
        try {
            const message = JSON.parse(event.data);
            if (message.type === 'STATE_UPDATE') {
              setScannerState(message.payload);
            }
        } catch (e) {
            console.warn('Unknown message:', event.data);
        }
      }
    };

    return () => {
      if (ws.current) {
        ws.current.close();
      }
    };
  }, []);

  return (
    <ThemeProvider theme={darkTheme}>
      <CssBaseline />
      <AppBar position="static" color="transparent" elevation={0} sx={{ borderBottom: '1px solid #333' }}>
        <Toolbar>
          <Typography variant="h6" component="div" sx={{ flexGrow: 1, color: '#00ff00', fontWeight: 'bold' }}>
            OPENSCANNER P25
          </Typography>
        </Toolbar>
      </AppBar>

      <Container maxWidth="md" sx={{ mt: 4 }}>
        <Stack spacing={3}>
          <Box>
            <ScannerDisplay state={scannerState} />
          </Box>
          
          <Box>
            <Typography variant="h6" gutterBottom color="primary">Monitored Channels</Typography>
            {channels.map((ch) => (
              <div key={ch.frequency} style={{ 
                padding: '10px', 
                borderBottom: '1px solid #333',
                opacity: scannerState.currentChannel?.frequency === ch.frequency ? 1 : 0.5,
                backgroundColor: scannerState.currentChannel?.frequency === ch.frequency ? 'rgba(0, 255, 0, 0.1)' : 'transparent'
              }}>
                <div style={{ display: 'flex', justifyContent: 'space-between' }}>
                  <span style={{ fontWeight: 'bold' }}>{ch.alphaTag}</span>
                  <span>{ch.frequency} MHz</span>
                </div>
                <div style={{ fontSize: '0.8rem', color: '#aaa' }}>{ch.description}</div>
              </div>
            ))}
          </Box>
        </Stack>
      </Container>
    </ThemeProvider>
  );
}

export default App;