import React, { useEffect, useRef } from 'react';
import { Box } from '@mui/material';
import { accent, status, surface } from '../theme/tokens';

interface Props {
    analyser?: AnalyserNode;
    height?: number;
    width?: number;
}

const VuMeter: React.FC<Props> = ({ analyser, height = 200, width = 20 }) => {
    const canvasRef = useRef<HTMLCanvasElement>(null);
    const animationRef = useRef<number | undefined>(undefined);

    useEffect(() => {
        if (!canvasRef.current || !analyser) return;

        const canvas = canvasRef.current;
        const ctx = canvas.getContext('2d');
        if (!ctx) return;

        const dataArray = new Uint8Array(analyser.fftSize);

        const draw = () => {
            if (!ctx) return;

            // Get time domain data for volume (waveform)
            analyser.getByteTimeDomainData(dataArray);

            // Calculate RMS (Root Mean Square) -> Volume
            let sum = 0;
            for (let i = 0; i < dataArray.length; i++) {
                const x = (dataArray[i] - 128) / 128.0; // Normalize to -1..1
                sum += x * x;
            }
            const rms = Math.sqrt(sum / dataArray.length);
            
            // Boost value for visibility (RMS is usually low)
            const volume = Math.min(1, rms * 5); 

            // Clear
            ctx.clearRect(0, 0, width, height);

            // Draw LED Bars
            // Calculate number of bars based on available height to ensure visibility
            // aim for ~3px per bar (2px height + 1px gap)
            const bars = Math.max(5, Math.floor(height / 3)); 
            const gap = 1;
            const barHeight = (height - (bars * gap)) / bars;
            
            for (let i = 0; i < bars; i++) {
                // Calculate threshold for this bar (bottom is 0, top is 1)
                const threshold = i / bars;
                
                // Semantic VU ramp: green → amber → red near the top; dim when inactive.
                let color: string = surface.raised;
                if (volume > threshold) {
                    if (i > bars * 0.8) color = status.error;
                    else if (i > bars * 0.6) color = status.warn;
                    else color = accent.main;
                }

                ctx.fillStyle = color;
                // Draw from bottom up
                const y = height - ((i + 1) * (barHeight + gap));
                ctx.fillRect(0, y, width, barHeight);
            }

            animationRef.current = requestAnimationFrame(draw);
        };

        draw();

        return () => {
            if (animationRef.current) cancelAnimationFrame(animationRef.current);
        };
    }, [analyser, width, height]);

    return (
        <Box sx={{
            border: '1px solid',
            borderColor: 'surface.border',
            bgcolor: '#000',
            p: 0.5,
            borderRadius: 1,
            display: 'inline-block'
        }}>
            <canvas ref={canvasRef} width={width} height={height} />
        </Box>
    );
};

export default VuMeter;