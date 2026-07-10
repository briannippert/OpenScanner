import React, { useEffect, useRef } from 'react';
import { Box } from '@mui/material';
import { viridisRGB } from '../viz/ramp';

interface Props {
    analyser?: AnalyserNode;
    height?: number;
}

const AudioSpectrogram: React.FC<Props> = ({ analyser, height = 150 }) => {
    const canvasRef = useRef<HTMLCanvasElement>(null);
    const animationRef = useRef<number | undefined>(undefined);

    useEffect(() => {
        if (!canvasRef.current || !analyser) return;

        const canvas = canvasRef.current;
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        if (!ctx) return;

        // Configuration
        // const fftSize = 1024; // Resolution of frequency bars
        // analyser.fftSize = fftSize; // Removed to avoid prop mutation
        const bufferLength = analyser.frequencyBinCount;
        const dataArray = new Uint8Array(bufferLength);

        // Off-screen buffer for scrolling effect
        const tempCanvas = document.createElement('canvas');
        tempCanvas.width = canvas.width;
        tempCanvas.height = canvas.height;
        const tempCtx = tempCanvas.getContext('2d');

        const draw = () => {
            if (!ctx || !tempCtx) return;

            // 1. Get Audio Data
            analyser.getByteFrequencyData(dataArray);

            // 2. Shift existing image down by 1 pixel
            tempCtx.drawImage(canvas, 0, 0);
            ctx.fillStyle = '#000'; // Background color
            ctx.fillRect(0, 0, canvas.width, canvas.height);
            ctx.drawImage(tempCanvas, 0, 1); // Draw old image 1px lower

            // 3. Draw new row at the top (y=0)
            const imageData = ctx.createImageData(canvas.width, 1);
            
            // Map FFT bins to Canvas Width
            // bufferLength (512) -> canvas.width
            for (let x = 0; x < canvas.width; x++) {
                // Logarithmic scale for better audio visualization
                // or Linear scale for simplicity. Let's do linear for raw PCM.
                const i = Math.floor((x / canvas.width) * (bufferLength / 2)); // Use lower half of spectrum (0-4khz)
                const value = dataArray[i];

                // Perceptual (viridis) magnitude ramp; below the noise floor stays black.
                let r = 0, g = 0, b = 0;
                const FLOOR = 18;
                if (value >= FLOOR) {
                    [r, g, b] = viridisRGB((value - FLOOR) / (255 - FLOOR));
                }

                const pixelIndex = x * 4;
                imageData.data[pixelIndex] = r;
                imageData.data[pixelIndex + 1] = g;
                imageData.data[pixelIndex + 2] = b;
                imageData.data[pixelIndex + 3] = 255; // Alpha
            }
            
            ctx.putImageData(imageData, 0, 0);

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
                width={800} 
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
