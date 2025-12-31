import express from 'express';
import http from 'http';
import { WebSocketServer, WebSocket } from 'ws';
import cors from 'cors';
import { MockRadio } from './scanner/MockRadio';
import { RtlDevice } from './scanner/RtlDevice';
import { CHANNELS } from './models';

const app = express();
app.use(cors());
app.use(express.json());

const server = http.createServer(app);
const wss = new WebSocketServer({ server });

// Choose driver based on environment variable
const useRealRadio = process.env.USE_REAL_RADIO === 'true';
const radio = useRealRadio ? new RtlDevice() : new MockRadio();

// Global error handler to prevent server crash if radio fails before client connects
radio.on('error', (err) => {
    console.error('Radio Error:', err);
});

console.log(`Initializing Radio Driver: ${useRealRadio ? 'REAL HARDWARE (RTL-SDR)' : 'MOCK SIMULATION'}`);

// API Routes
app.get('/api/channels', (req, res) => {
    res.json(CHANNELS);
});

app.post('/api/control', (req, res) => {
    const { action, frequency } = req.body;
    if (action === 'start') {
        radio.start();
        res.json({ message: 'Scanner started' });
    } else if (action === 'stop') {
        radio.stop();
        res.json({ message: 'Scanner stopped' });
    } else if (action === 'hold' && frequency) {
        radio.holdFrequency(parseFloat(frequency));
        res.json({ message: `Holding on ${frequency}` });
    } else if (action === 'scan') {
        radio.resumeScan();
        res.json({ message: 'Resuming scan' });
    } else {
        res.status(400).json({ error: 'Invalid action' });
    }
});

// WebSocket handling
wss.on('connection', (ws) => {
    console.log('Client connected');
    
    // Send initial state
    ws.send(JSON.stringify({ type: 'STATE_UPDATE', payload: radio.getState() }));

    const handleStateChange = (state: any) => {
        if (ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify({ type: 'STATE_UPDATE', payload: state }));
        }
    };

    radio.on('state-change', handleStateChange);
    
    // Broadcast audio chunks
    const handleAudio = (chunk: Buffer) => {
        if (ws.readyState === WebSocket.OPEN) {
            ws.send(chunk);
        }
    };
    radio.on('audio', handleAudio);
    
    // Forward hardware errors to frontend
    const handleError = (err: any) => {
        if (ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify({ type: 'ERROR', payload: err }));
        }
    };
    radio.on('error', handleError);

    ws.on('close', () => {
        radio.off('state-change', handleStateChange);
        radio.off('audio', handleAudio);
        radio.off('error', handleError);
        console.log('Client disconnected');
    });
});

const PORT = 3001;
server.listen(PORT, '0.0.0.0', () => {
    console.log(`Server running on http://0.0.0.0:${PORT}`);
    // Auto-start radio for demo purposes
    radio.start();
});
