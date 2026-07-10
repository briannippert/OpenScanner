import React, { useEffect, useRef } from 'react';
import { Box } from '@mui/material';
import { viridisRGB } from '../viz/ramp';

interface Props {
    analyser?: AnalyserNode;
    height?: number;
}

const WIDTH = 800;

const AudioSpectrogram: React.FC<Props> = ({ analyser, height = 150 }) => {
    const canvasRef = useRef<HTMLCanvasElement>(null);
    const animationRef = useRef<number | undefined>(undefined);

    useEffect(() => {
        if (!canvasRef.current || !analyser) return;

        const canvas = canvasRef.current;
        const H = canvas.height;
        const ctx = canvas.getContext('2d');
        if (!ctx) return;
        ctx.imageSmoothingEnabled = false;

        const bufferLength = analyser.frequencyBinCount;
        const dataArray = new Uint8Array(bufferLength);

        // Offscreen ring-buffer history: we only ever write ONE new row per frame
        // (at a moving top pointer) and render the scroll with two cheap blits.
        // This avoids copying the whole canvas onto itself every frame, which is
        // what made the old approach janky.
        const hist = document.createElement('canvas');
        hist.width = WIDTH;
        hist.height = H;
        const histCtx = hist.getContext('2d');

        // A reusable 1px-tall row we putImageData into, then blit into the history.
        const rowCanvas = document.createElement('canvas');
        rowCanvas.width = WIDTH;
        rowCanvas.height = 1;
        const rowCtx = rowCanvas.getContext('2d');
        const rowData = rowCtx ? rowCtx.createImageData(WIDTH, 1) : null;

        let top = 0; // ring y-index that maps to the newest row (visible y=0)

        const draw = () => {
            if (!ctx || !histCtx || !rowCtx || !rowData) return;

            analyser.getByteFrequencyData(dataArray);

            const FLOOR = 18;
            for (let x = 0; x < WIDTH; x++) {
                // Lower half of the spectrum (~0–4 kHz voice band).
                const i = Math.floor((x / WIDTH) * (bufferLength / 2));
                const value = dataArray[i];
                let r = 0, g = 0, b = 0;
                if (value >= FLOOR) {
                    [r, g, b] = viridisRGB((value - FLOOR) / (255 - FLOOR));
                }
                const p = x * 4;
                rowData.data[p] = r;
                rowData.data[p + 1] = g;
                rowData.data[p + 2] = b;
                rowData.data[p + 3] = 255;
            }
            rowCtx.putImageData(rowData, 0, 0);

            // Advance the ring up by one and write the newest row there.
            top = (top - 1 + H) % H;
            histCtx.drawImage(rowCanvas, 0, top);

            // Render newest-at-top: [top..H] then [0..top].
            const lower = H - top;
            ctx.drawImage(hist, 0, top, WIDTH, lower, 0, 0, WIDTH, lower);
            if (top > 0) {
                ctx.drawImage(hist, 0, 0, WIDTH, top, 0, lower, WIDTH, top);
            }

            animationRef.current = requestAnimationFrame(draw);
        };

        draw();

        return () => {
            if (animationRef.current) cancelAnimationFrame(animationRef.current);
        };
    }, [analyser]);

    return (
        <Box sx={{
            width: '100%',
            height: height,
            border: '1px solid',
            borderColor: 'surface.border',
            borderRadius: 1,
            overflow: 'hidden',
            bgcolor: '#000',
            position: 'relative'
        }}>
            <canvas
                ref={canvasRef}
                width={WIDTH}
                height={height}
                style={{ width: '100%', height: '100%', display: 'block' }}
            />
            <div style={{
                position: 'absolute',
                top: 5, right: 10,
                color: 'rgba(255,255,255,0.5)',
                fontSize: '10px',
                fontFamily: 'monospace',
                pointerEvents: 'none'
            }}>
                AUDIO SPECTRUM (0 - 4kHz)
            </div>
        </Box>
    );
};

export default AudioSpectrogram;
