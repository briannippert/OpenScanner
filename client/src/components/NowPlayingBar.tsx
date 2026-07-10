import React, { useEffect, useRef } from 'react';
import { Box, Typography, IconButton, Tooltip, Button } from '@mui/material';
import { useTheme, alpha } from '@mui/material/styles';
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import PauseIcon from '@mui/icons-material/Pause';
import CloseIcon from '@mui/icons-material/Close';
import DownloadIcon from '@mui/icons-material/Download';
import GraphicEqIcon from '@mui/icons-material/GraphicEq';
import type { NowPlaying } from '../hooks/useAudioPipeline';

interface Props {
  nowPlaying: NowPlaying;
  isPaused: boolean;
  positionSec: number;
  playbackRate: number;
  onTogglePause: () => void;
  onSeek: (sec: number) => void;
  onSetRate: (rate: number) => void;
  onStop: () => void;
}

const RATES = [1, 1.25, 1.5, 2, 0.5, 0.75];

const fmt = (s: number) => {
  if (!isFinite(s) || s < 0) s = 0;
  const m = Math.floor(s / 60);
  const sec = Math.floor(s % 60);
  return `${m}:${sec.toString().padStart(2, '0')}`;
};

const Waveform: React.FC<{ peaks: number[]; progress: number; color: string; onSeek: (fraction: number) => void }> = ({ peaks, progress, color, onSeek }) => {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const theme = useTheme();
  const draggingRef = useRef(false);

  useEffect(() => {
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

    const n = peaks.length;
    if (n === 0) return;
    const barW = w / n;
    const gap = Math.min(1, barW * 0.35);
    const mid = h / 2;
    const played = progress * w;
    const mutedColor = theme.palette.surface.borderStrong;

    for (let i = 0; i < n; i++) {
      const x = i * barW;
      const barH = Math.max(2, peaks[i] * (h - 2));
      ctx.fillStyle = x < played ? color : mutedColor;
      ctx.fillRect(x + gap / 2, mid - barH / 2, Math.max(1, barW - gap), barH);
    }
  }, [peaks, progress, color, theme]);

  const seekFromEvent = (clientX: number) => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    onSeek(Math.max(0, Math.min(1, (clientX - rect.left) / rect.width)));
  };

  return (
    <canvas
      ref={canvasRef}
      role="slider"
      aria-label="Seek"
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={Math.round(progress * 100)}
      style={{ width: '100%', height: 36, display: 'block', cursor: 'pointer', touchAction: 'none' }}
      onPointerDown={(e) => { draggingRef.current = true; e.currentTarget.setPointerCapture(e.pointerId); seekFromEvent(e.clientX); }}
      onPointerMove={(e) => { if (draggingRef.current) seekFromEvent(e.clientX); }}
      onPointerUp={(e) => { draggingRef.current = false; e.currentTarget.releasePointerCapture(e.pointerId); }}
    />
  );
};

const NowPlayingBar: React.FC<Props> = ({ nowPlaying, isPaused, positionSec, playbackRate, onTogglePause, onSeek, onSetRate, onStop }) => {
  const theme = useTheme();
  const accent = theme.palette.primary.main;
  const progress = nowPlaying.duration > 0 ? positionSec / nowPlaying.duration : 0;

  const cycleRate = () => {
    const idx = RATES.indexOf(playbackRate);
    onSetRate(RATES[(idx + 1) % RATES.length] ?? 1);
  };

  return (
    <Box
      sx={{
        flexShrink: 0,
        display: 'flex',
        alignItems: 'center',
        gap: { xs: 1, sm: 2 },
        px: { xs: 1, sm: 2 },
        py: 1,
        borderTop: '1px solid',
        borderColor: 'surface.border',
        bgcolor: 'surface.raised',
        animation: 'rowEnter 200ms ease',
      }}
    >
      <GraphicEqIcon sx={{ color: 'primary.main', flexShrink: 0, display: { xs: 'none', sm: 'block' } }} />
      <Box sx={{ minWidth: { xs: 90, sm: 140 }, maxWidth: 200, flexShrink: 0 }}>
        <Typography variant="body2" fontWeight={700} noWrap>{nowPlaying.label || 'Recording'}</Typography>
        <Typography variant="caption" color="text.secondary" noWrap sx={{ fontFamily: (t) => t.typography.mono.fontFamily }}>
          {fmt(positionSec)} / {fmt(nowPlaying.duration)}
        </Typography>
      </Box>

      <Tooltip title={isPaused ? 'Play' : 'Pause'}>
        <IconButton onClick={onTogglePause} sx={{ color: 'primary.main', flexShrink: 0 }} aria-label={isPaused ? 'Play' : 'Pause'}>
          {isPaused ? <PlayArrowIcon /> : <PauseIcon />}
        </IconButton>
      </Tooltip>

      <Box sx={{ flexGrow: 1, minWidth: 0 }}>
        <Waveform peaks={nowPlaying.peaks} progress={progress} color={accent} onSeek={(f) => onSeek(f * nowPlaying.duration)} />
      </Box>

      <Tooltip title="Playback speed">
        <Button
          size="small"
          variant="outlined"
          color="inherit"
          onClick={cycleRate}
          sx={{ minWidth: 48, flexShrink: 0, fontFamily: (t) => t.typography.mono.fontFamily, borderColor: alpha(accent, 0.4) }}
        >
          {playbackRate}×
        </Button>
      </Tooltip>
      <Tooltip title="Download">
        <IconButton
          component="a"
          href={`/audio/${nowPlaying.filename}`}
          download={nowPlaying.filename.split('/').pop() || 'recording.wav'}
          sx={{ color: 'text.secondary', flexShrink: 0, display: { xs: 'none', sm: 'inline-flex' } }}
          aria-label="Download recording"
        >
          <DownloadIcon />
        </IconButton>
      </Tooltip>
      <Tooltip title="Close">
        <IconButton onClick={onStop} sx={{ color: 'text.secondary', flexShrink: 0 }} aria-label="Stop playback">
          <CloseIcon />
        </IconButton>
      </Tooltip>
    </Box>
  );
};

export default NowPlayingBar;
