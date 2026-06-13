import React, { useEffect, useRef, useState } from 'react';
import { Box, Typography, Paper } from '@mui/material';

interface DataPoint {
    frequency: number;
    db: number;
}

interface Props {
    data?: DataPoint[];
    height?: number;
}

const RfWaterfallDebug: React.FC<Props> = ({ data, height = 500 }) => {
    const canvasRef = useRef<HTMLCanvasElement>(null);
    const historyRef = useRef<HTMLCanvasElement | null>(null);
    const lastDataRef = useRef<string>('');
    const [hoverInfo, setHoverInfo] = useState<{ x: number, freq: number, db: number } | null>(null);
    const [tooltipFlip, setTooltipFlip] = useState(false);

    useEffect(() => {
        if (!canvasRef.current || !data || data.length === 0) return;

        // Use more points for better stability check in debug mode
        const dataStr = JSON.stringify(data.slice(0, 10)); 
        if (dataStr === lastDataRef.current) return;
        lastDataRef.current = dataStr;

        const width = 1024; // Matched to FFT size
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
            const minDb = -110;
            const maxDb = 0;

            for (let x = 0; x < data.length; x++) {
                const db = data[x].db;
                const val = Math.max(0, Math.min(255, ((db - minDb) / (maxDb - minDb)) * 255));

                let r = 0, g = 0, b = 0;
                
                // Classic Jet-like colormap
                if (val < 64) {
                    b = val * 4;
                } else if (val < 128) {
                    b = 255;
                    g = (val - 64) * 4;
                } else if (val < 192) {
                    g = 255;
                    b = 255 - (val - 128) * 4;
                    r = (val - 128) * 2; // Add some red early
                } else {
                    r = 255;
                    g = 255 - (val - 192) * 4;
                    b = 0;
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
            
            // Draw Center Line
            ctx.strokeStyle = 'rgba(255, 255, 255, 0.3)';
            ctx.setLineDash([5, 5]);
            ctx.lineWidth = 1;
            ctx.beginPath();
            ctx.moveTo(width / 2, 0);
            ctx.lineTo(width / 2, canHeight);
            ctx.stroke();
            ctx.setLineDash([]);
        }

    }, [data, height]);

    const handlePointerMove = (e: React.PointerEvent<HTMLDivElement>) => {
        if (!data || data.length === 0) return;
        
        const rect = e.currentTarget.getBoundingClientRect();
        const mouseX = e.clientX - rect.left;
        const width = rect.width;

        setTooltipFlip(mouseX > width * 0.7);

        // Map mouse X to data index
        const index = Math.floor((mouseX / width) * data.length);
        if (index >= 0 && index < data.length) {
            setHoverInfo({
                x: mouseX,
                freq: data[index].frequency,
                db: data[index].db
            });
        }
    };

    const minFreq = data && data.length > 0 ? data[0].frequency.toFixed(3) : '---';
    const maxFreq = data && data.length > 0 ? data[data.length - 1].frequency.toFixed(3) : '---';
    const centerFreq = data && data.length > 0 ? data[Math.floor(data.length / 2)].frequency.toFixed(3) : '---';

    return (
        <Box 
            onPointerMove={handlePointerMove}
            onPointerLeave={() => setHoverInfo(null)}
            sx={{ 
                width: '100%', 
                height: height, 
                border: '2px solid #444', 
                borderRadius: 1, 
                overflow: 'hidden',
                bgcolor: '#000',
                position: 'relative',
                boxShadow: '0 0 20px rgba(0,0,0,0.5)',
                cursor: 'crosshair',
                touchAction: 'none' // Prevent scrolling while interacting
            }}
        >
            <canvas 
                ref={canvasRef} 
                width={1024} 
                height={height} 
                style={{ width: '100%', height: '100%', display: 'block', pointerEvents: 'none' }} 
            />
            
            {/* Frequency Markers Overlay */}
            <Box sx={{
                position: 'absolute',
                top: 0, left: 0, right: 0,
                height: '25px',
                background: 'rgba(0,0,0,0.7)',
                borderBottom: '1px solid #333',
                display: 'flex',
                justifyContent: 'space-between',
                alignItems: 'center',
                padding: '0 10px',
                fontFamily: 'monospace',
                fontSize: '11px',
                color: '#aaa',
                pointerEvents: 'none',
                zIndex: 10
            }}>
                <span>{minFreq} MHz</span>
                <span style={{ color: '#00ff00', fontWeight: 'bold' }}>{centerFreq} MHz (CENTER)</span>
                <span>{maxFreq} MHz</span>
            </Box>

            {/* Hover Cursor/Tooltip */}
            {hoverInfo && (
                <>
                    <Box sx={{
                        position: 'absolute',
                        top: 25,
                        bottom: 0,
                        left: hoverInfo.x,
                        width: '1px',
                        bgcolor: 'rgba(255, 255, 255, 0.7)',
                        pointerEvents: 'none',
                        zIndex: 5,
                        boxShadow: '0 0 5px rgba(255,255,255,0.5)'
                    }} />
                    <Paper sx={{
                        position: 'absolute',
                        top: 35,
                        left: hoverInfo.x + 15,
                        transform: tooltipFlip ? 'translateX(-110%)' : 'none',
                        p: 1.5,
                        bgcolor: 'rgba(10, 10, 10, 0.95)',
                        border: '2px solid #00ff00',
                        pointerEvents: 'none',
                        zIndex: 20,
                        minWidth: 160,
                        boxShadow: '0 4px 15px rgba(0,0,0,0.5)'
                    }}>
                        <Typography variant="body2" sx={{ color: '#00ff00', fontWeight: 'bold', display: 'block', fontFamily: 'monospace', fontSize: '13px' }}>
                            {hoverInfo.freq.toFixed(4)} MHz
                        </Typography>
                        <Typography variant="body2" sx={{ color: '#fff', display: 'block', fontFamily: 'monospace', fontSize: '12px', mt: 0.5 }}>
                            {hoverInfo.db.toFixed(1)} dB
                        </Typography>
                    </Paper>
                </>
            )}

            <Box sx={{
                position: 'absolute',
                bottom: 10, right: 10,
                color: 'rgba(0,255,0,0.5)',
                fontSize: '10px',
                fontFamily: 'monospace',
                pointerEvents: 'none',
                background: 'rgba(0,0,0,0.4)',
                padding: '2px 6px',
                borderRadius: '4px',
                zIndex: 10
            }}>
                2.4 MHz SPAN @ 1024 BINS
            </Box>
        </Box>
    );
};

export default RfWaterfallDebug;
