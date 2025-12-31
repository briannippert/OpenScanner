import React, { useEffect, useRef } from 'react';
import { Box } from '@mui/material';

interface DataPoint {
    frequency: number;
    db: number;
}

interface Props {
    data?: DataPoint[];
    height?: number;
}

const RfSpectrum: React.FC<Props> = ({ data, height = 150 }) => {
    const canvasRef = useRef<HTMLCanvasElement>(null);
    const historyRef = useRef<HTMLCanvasElement | null>(null);
    const lastDataRef = useRef<string>('');

    useEffect(() => {
        if (!canvasRef.current || !data || data.length === 0) return;

        const dataStr = JSON.stringify(data.slice(0, 5)); 
        if (dataStr === lastDataRef.current) return;
        lastDataRef.current = dataStr;

        const width = 800; // Fixed internal width
        const canHeight = height;

        // Initialize history canvas if needed
        if (!historyRef.current) {
            historyRef.current = document.createElement('canvas');
            historyRef.current.width = width;
            historyRef.current.height = canHeight;
            const hCtx = historyRef.current.getContext('2d');
            if (hCtx) {
                hCtx.fillStyle = '#000';
                hCtx.fillRect(0, 0, width, canHeight);
            }
        }

        const hCanvas = historyRef.current;
        const hCtx = hCanvas.getContext('2d', { willReadFrequently: true });
        if (!hCtx) return;

        // 1. Shift history down
        const tempCanvas = document.createElement('canvas');
        tempCanvas.width = width;
        tempCanvas.height = canHeight;
        const tempCtx = tempCanvas.getContext('2d');
        if (tempCtx) {
            tempCtx.drawImage(hCanvas, 0, 0);
            hCtx.fillStyle = '#000';
            hCtx.fillRect(0, 0, width, canHeight);
            hCtx.drawImage(tempCanvas, 0, 1);
        }

        // 2. Draw new row to history
        const rowCanvas = document.createElement('canvas');
        rowCanvas.width = data.length;
        rowCanvas.height = 1;
        const rowCtx = rowCanvas.getContext('2d');
        if (rowCtx) {
            const imageData = rowCtx.createImageData(data.length, 1);
            const minDb = -100; // Lowered floor
            const maxDb = -20;  // Adjusted ceiling

            for (let x = 0; x < data.length; x++) {
                const db = data[x].db;
                const val = Math.max(0, Math.min(255, ((db - minDb) / (maxDb - minDb)) * 255));

                let r = 0, g = 0, b = 0;
                // Improved Heatmap: DarkBlue -> Cyan -> Green -> Yellow -> Red
                if (val < 50) {
                    b = val * 5;
                } else if (val < 100) {
                    b = 255;
                    g = (val - 50) * 5;
                } else if (val < 150) {
                    g = 255;
                    b = 255 - (val - 100) * 5;
                } else if (val < 200) {
                    g = 255;
                    r = (val - 150) * 5;
                } else {
                    r = 255;
                    g = 255 - (val - 200) * 5;
                }

                const i = x * 4;
                imageData.data[i] = r;
                imageData.data[i + 1] = g;
                imageData.data[i + 2] = b;
                imageData.data[i + 3] = 255;
            }
            rowCtx.putImageData(imageData, 0, 0);
            hCtx.drawImage(rowCanvas, 0, 0, data.length, 1, 0, 0, width, 1);
        }

        // 3. Copy history to visible canvas
        const ctx = canvasRef.current.getContext('2d');
        if (ctx) {
            ctx.drawImage(hCanvas, 0, 0);
        }

    }, [data, height]);

    const minFreq = data && data.length > 0 ? data[0].frequency.toFixed(2) : '---';
    const maxFreq = data && data.length > 0 ? data[data.length - 1].frequency.toFixed(2) : '---';

    return (
        <Box sx={{ 
            width: '100%', 
            height: height, 
            border: '1px solid #333', 
            borderRadius: 1, 
            overflow: 'hidden',
            bgcolor: '#000',
            position: 'relative'
        }}>
            <canvas 
                ref={canvasRef} 
                width={800} 
                height={height} 
                style={{ width: '100%', height: '100%', display: 'block' }} 
            />
            <div style={{
                position: 'absolute',
                top: 5, left: 10,
                color: 'rgba(255,255,255,0.9)',
                fontSize: '10px',
                fontFamily: 'monospace',
                pointerEvents: 'none',
                textShadow: '1px 1px 2px black',
                display: 'flex',
                gap: '20px',
                background: 'rgba(0,0,0,0.4)',
                padding: '2px 6px',
                borderRadius: '4px'
            }}>
                <span>RF WATERFALL</span>
                <span style={{color: '#00ff00'}}>{minFreq} MHz - {maxFreq} MHz</span>
            </div>
        </Box>
    );
};

export default RfSpectrum;