import React, { useEffect, useRef } from 'react';
import { Box } from '@mui/material';

interface Props {
  /** Latest signal value (0–100). Sampled into a rolling history on change. */
  value: number;
  color: string;
  height?: number;
  /** Number of samples kept in the rolling window. */
  points?: number;
}

/**
 * Rolling sparkline of recent signal strength. Self-contained: it keeps its own
 * ring buffer of the last `points` values and redraws on each new sample, giving
 * a sense of signal history rather than a single instantaneous bar.
 */
const SignalSparkline: React.FC<Props> = ({ value, color, height = 22, points = 96 }) => {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const history = useRef<number[]>([]);

  useEffect(() => {
    const buf = history.current;
    buf.push(Math.max(0, Math.min(100, value)));
    if (buf.length > points) buf.shift();

    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    const dpr = window.devicePixelRatio || 1;
    const w = canvas.clientWidth;
    const h = canvas.clientHeight;
    if (canvas.width !== w * dpr || canvas.height !== h * dpr) {
      canvas.width = w * dpr;
      canvas.height = h * dpr;
    }
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);

    const n = buf.length;
    if (n < 2) return;
    const step = w / (points - 1);
    const y = (v: number) => h - (v / 100) * (h - 2) - 1;
    const x0 = w - (n - 1) * step;

    // Filled area under the line.
    ctx.beginPath();
    ctx.moveTo(x0, h);
    for (let i = 0; i < n; i++) ctx.lineTo(x0 + i * step, y(buf[i]));
    ctx.lineTo(x0 + (n - 1) * step, h);
    ctx.closePath();
    const grad = ctx.createLinearGradient(0, 0, 0, h);
    grad.addColorStop(0, hexWithAlpha(color, 0.35));
    grad.addColorStop(1, hexWithAlpha(color, 0));
    ctx.fillStyle = grad;
    ctx.fill();

    // Line.
    ctx.beginPath();
    for (let i = 0; i < n; i++) {
      const px = x0 + i * step;
      const py = y(buf[i]);
      if (i === 0) ctx.moveTo(px, py);
      else ctx.lineTo(px, py);
    }
    ctx.strokeStyle = color;
    ctx.lineWidth = 1.5;
    ctx.lineJoin = 'round';
    ctx.stroke();
  }, [value, points, color]);

  return (
    <Box sx={{ flex: 1, minWidth: 0 }}>
      <canvas ref={canvasRef} style={{ width: '100%', height, display: 'block' }} />
    </Box>
  );
};

// Accept "#rrggbb" or any CSS color; for non-hex we fall back to a rgba wrapper.
function hexWithAlpha(color: string, alpha: number): string {
  if (color.startsWith('#') && (color.length === 7 || color.length === 4)) {
    let r: number, g: number, b: number;
    if (color.length === 7) {
      r = parseInt(color.slice(1, 3), 16);
      g = parseInt(color.slice(3, 5), 16);
      b = parseInt(color.slice(5, 7), 16);
    } else {
      r = parseInt(color[1] + color[1], 16);
      g = parseInt(color[2] + color[2], 16);
      b = parseInt(color[3] + color[3], 16);
    }
    return `rgba(${r},${g},${b},${alpha})`;
  }
  return color;
}

export default SignalSparkline;
