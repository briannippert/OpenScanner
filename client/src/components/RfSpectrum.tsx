import React, { useEffect, useRef } from 'react';
import { Box } from '@mui/material';
import { viridisRGB } from '../viz/ramp';

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
    const queueRef = useRef<DataPoint[][]>([]);
    const animationRef = useRef<number>(0);
    const lastDataRef = useRef<string>('');

    // Push new data to queue
    useEffect(() => {
        if (!data || data.length === 0) return;
        
        // Basic stability check
        const dataStr = JSON.stringify(data.slice(0, 5)); 
        if (dataStr === lastDataRef.current) return;
        lastDataRef.current = dataStr;

        queueRef.current.push(data);
        
        // Cap the queue to prevent memory issues if hidden for a long time
        if (queueRef.current.length > 500) {
            queueRef.current.shift();
        }
    }, [data]);

    useEffect(() => {
        const width = 800; // Fixed internal width
        const canHeight = height;

        const drawRowToHistory = (rowData: DataPoint[]) => {
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
            hCtx.drawImage(hCanvas, 0, 0, width, canHeight, 0, 1, width, canHeight);
            
            // 2. Clear the top row
            hCtx.fillStyle = '#000';
            hCtx.fillRect(0, 0, width, 1);

            // 3. Draw new row
            const imageData = hCtx.createImageData(rowData.length, 1);
            const minDb = -100;
            const maxDb = -20;

            for (let x = 0; x < rowData.length; x++) {
                const db = rowData[x].db;
                const t = Math.max(0, Math.min(1, (db - minDb) / (maxDb - minDb)));
                const [r, g, b] = viridisRGB(t);

                const i = x * 4;
                imageData.data[i] = r;
                imageData.data[i + 1] = g;
                imageData.data[i + 2] = b;
                imageData.data[i + 3] = 255;
            }
            
            // Draw the computed row to a temp canvas to stretch it if necessary
            const rowCanvas = document.createElement('canvas');
            rowCanvas.width = rowData.length;
            rowCanvas.height = 1;
            const rowCtx = rowCanvas.getContext('2d');
            if (rowCtx) {
                rowCtx.putImageData(imageData, 0, 0);
                hCtx.drawImage(rowCanvas, 0, 0, rowData.length, 1, 0, 0, width, 1);
            }
        };

        const render = () => {
            if (queueRef.current.length > 0) {
                // If we have a massive backlog, process up to 10 rows per frame
                // to "catch up" quickly but not all at once.
                const rowsToProcess = Math.min(queueRef.current.length, 10);
                for (let i = 0; i < rowsToProcess; i++) {
                    const nextRow = queueRef.current.shift();
                    if (nextRow) drawRowToHistory(nextRow);
                }

                // Update visible canvas
                if (canvasRef.current && historyRef.current) {
                    const ctx = canvasRef.current.getContext('2d');
                    if (ctx) {
                        ctx.drawImage(historyRef.current, 0, 0);
                    }
                }
            }
            animationRef.current = requestAnimationFrame(render);
        };

        animationRef.current = requestAnimationFrame(render);
        
        return () => {
            if (animationRef.current) cancelAnimationFrame(animationRef.current);
        };
    }, [height]);

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
