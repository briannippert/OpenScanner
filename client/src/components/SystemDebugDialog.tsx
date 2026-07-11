import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Box, Typography, Button, Paper, Grid, CircularProgress, Tooltip,
} from '@mui/material';
import { useTheme } from '@mui/material/styles';
import BugReportIcon from '@mui/icons-material/BugReport';
import RefreshIcon from '@mui/icons-material/Refresh';
import SupportAgentIcon from '@mui/icons-material/SupportAgent';
import FormDialog from './common/FormDialog';
import StatusChip from './common/StatusChip';
import type { StatusTone } from './common/StatusChip';
import { apiFetch, apiJson } from './common/apiBase';

interface TranscriptionQueueStatus { queued: number; workers: number; }
interface SystemStats {
  cpuPercent: number;
  memPercent: number;
  memUsedMb: number;
  memTotalMb: number;
  transcription: TranscriptionQueueStatus;
}
interface ServiceStatus { name: string; state: string; detail: string; }
interface ListeningPort { protocol: string; port: number; process: string; }
interface ServicesSnapshot { services: ServiceStatus[]; ports: ListeningPort[]; }

interface GpsLocation {
  lat: number; lon: number; alt: number; speed: number;
  time: string; fix: number; sats: number; satsVisible: number; hdop?: number | null;
}
interface DiagnosticsSnapshot {
  uptime: string;
  scanner: {
    status: string; hardwareConnected: boolean; frequency?: number | null;
    signalDb?: number | null; signalStrength: number; gain?: number | null;
    squelch?: number | null; audioStreaming: boolean;
  };
  gps: { gpsdConnected: boolean; secondsSinceFix?: number | null; location: GpsLocation | null };
  database: { totalRecordings: number; transcribed: number; pending: number; oldestUtc?: string | null; newestUtc?: string | null };
  connections: { controlClients: number; audioClients: number };
  recording: { activeCount: number; activeIds: string[] };
  radio: { restartCount: number; throughputKbps: number };
  cleanup: { lastRunUtc?: string | null; lastFreeBytes?: number | null; totalPurged: number };
  transcriptionModelStatus?: string | null;
}
interface StorageInfo {
  recordingsBytes: number; recordingsCount: number; databaseBytes: number;
  diskFreeBytes: number; diskTotalBytes: number;
}
type SystemInfo = Record<string, string>;

// Byte / time formatting helpers.
const fmtBytes = (n: number): string => {
  if (!n || n < 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.min(units.length - 1, Math.floor(Math.log(n) / Math.log(1024)));
  return `${(n / 1024 ** i).toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
};
const fmtAge = (sec?: number | null): string => {
  if (sec == null) return '—';
  if (sec < 90) return `${Math.round(sec)}s ago`;
  if (sec < 5400) return `${Math.round(sec / 60)}m ago`;
  return `${Math.round(sec / 3600)}h ago`;
};
const fmtTs = (iso?: string | null): string => {
  if (!iso) return '—';
  const d = new Date(iso.includes('T') || iso.includes('Z') ? iso : iso.replace(' ', 'T') + 'Z');
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString([], { hour12: false });
};
const FIX_LABEL = ['No fix', 'No fix', '2D fix', '3D fix'];

interface Props {
  open: boolean;
  onClose: () => void;
  onDownloadSupport: () => void;
}

const MONO = '"Roboto Mono", ui-monospace, SFMono-Regular, Menlo, monospace';

// How many one-second samples to retain — the graphs cover the last 120 seconds.
const WINDOW = 120;

interface Sample { t: number; cpu: number; mem: number; }

/**
 * Single-series area+line chart over the last WINDOW seconds with a fixed 0-100%
 * y-domain and a hover crosshair. Text uses ink tokens; only the mark is colored.
 */
const ResourceGraph: React.FC<{
  label: string;
  color: string;
  samples: Sample[];
  value: (s: Sample) => number;
  currentText: string;
}> = ({ label, color, samples, value, currentText }) => {
  const theme = useTheme();
  const W = 520;
  const H = 140;
  const padL = 34;
  const padR = 8;
  const padT = 10;
  const padB = 16;
  const plotW = W - padL - padR;
  const plotH = H - padT - padB;

  const [hover, setHover] = useState<number | null>(null);

  // Map a sample index to an x coordinate. The window is right-aligned so the
  // newest sample sits at the right edge even before the buffer is full.
  const x = (i: number) => padL + plotW * (i - (samples.length - WINDOW)) / (WINDOW - 1);
  const y = (v: number) => padT + plotH * (1 - Math.max(0, Math.min(100, v)) / 100);

  const linePath = samples.map((s, i) => `${i === 0 ? 'M' : 'L'}${x(i).toFixed(1)},${y(value(s)).toFixed(1)}`).join(' ');
  const areaPath = samples.length
    ? `${linePath} L${x(samples.length - 1).toFixed(1)},${(padT + plotH).toFixed(1)} L${x(0).toFixed(1)},${(padT + plotH).toFixed(1)} Z`
    : '';

  const gridColor = theme.palette.surface.border;
  const inkMuted = theme.palette.text.secondary;

  const onMove = useCallback((e: React.MouseEvent<SVGSVGElement>) => {
    const rect = e.currentTarget.getBoundingClientRect();
    const px = (e.clientX - rect.left) / rect.width * W;
    if (px < padL || px > W - padR || samples.length === 0) { setHover(null); return; }
    const frac = (px - padL) / plotW;
    const idx = Math.round((samples.length - WINDOW) + frac * (WINDOW - 1));
    setHover(Math.max(0, Math.min(samples.length - 1, idx)));
  }, [samples.length, plotW]);

  const hoverSample = hover != null ? samples[hover] : undefined;

  return (
    <Box>
      <Box sx={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', mb: 0.5 }}>
        <Typography variant="caption" sx={{ color: inkMuted, fontWeight: 700, letterSpacing: 0.5, textTransform: 'uppercase' }}>
          {label}
        </Typography>
        <Typography variant="body2" sx={{ fontFamily: MONO, color: 'text.primary', fontWeight: 700 }}>
          {currentText}
        </Typography>
      </Box>
      <Box
        component="svg"
        viewBox={`0 0 ${W} ${H}`}
        onMouseMove={onMove}
        onMouseLeave={() => setHover(null)}
        sx={{ width: '100%', height: 'auto', display: 'block' }}
      >
        {[0, 25, 50, 75, 100].map((g) => (
          <g key={g}>
            <line x1={padL} x2={W - padR} y1={y(g)} y2={y(g)} stroke={gridColor} strokeWidth={1} opacity={0.5} />
            <text x={padL - 6} y={y(g) + 3} textAnchor="end" fontSize={9} fill={inkMuted} fontFamily={MONO}>{g}</text>
          </g>
        ))}
        {areaPath && <path d={areaPath} fill={color} opacity={0.14} />}
        {linePath && <path d={linePath} fill="none" stroke={color} strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />}
        {hoverSample && (
          <g>
            <line x1={x(hover!)} x2={x(hover!)} y1={padT} y2={padT + plotH} stroke={inkMuted} strokeWidth={1} opacity={0.6} />
            <circle cx={x(hover!)} cy={y(value(hoverSample))} r={3.5} fill={color} stroke={theme.palette.background.paper} strokeWidth={1.5} />
            <text
              x={Math.min(x(hover!) + 6, W - padR - 40)}
              y={padT + 10}
              fontSize={10}
              fontFamily={MONO}
              fill={theme.palette.text.primary}
            >
              {value(hoverSample).toFixed(0)}%
            </text>
          </g>
        )}
      </Box>
    </Box>
  );
};

const serviceTone = (state: string): StatusTone => {
  const s = state.toLowerCase();
  if (s.startsWith('active') || s.includes('running')) return 'success';
  if (s.startsWith('activating') || s.includes('reload')) return 'warn';
  if (s.startsWith('failed')) return 'error';
  return 'muted';
};

const modelStatusTone = (status?: string | null): StatusTone => {
  if (!status) return 'muted';
  if (status.startsWith('ready')) return 'success';
  if (status.startsWith('downloading')) return 'warn';
  if (status.startsWith('error')) return 'error';
  return 'muted';
};

// A label + mono value row used across the diagnostic panels.
const StatRow: React.FC<{ label: string; value: React.ReactNode; title?: string }> = ({ label, value, title }) => (
  <Box sx={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 2, minWidth: 0 }}>
    <Typography variant="caption" color="text.secondary" sx={{ flexShrink: 0 }}>{label}</Typography>
    <Tooltip title={title ?? ''} disableHoverListener={!title}>
      <Typography variant="body2" sx={{ fontFamily: MONO, fontWeight: 600, textAlign: 'right', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {value}
      </Typography>
    </Tooltip>
  </Box>
);

// Horizontal usage bar (0-1 fraction). Turns amber/red as it fills.
const UsageBar: React.FC<{ fraction: number }> = ({ fraction }) => {
  const theme = useTheme();
  const f = Math.max(0, Math.min(1, fraction));
  const color = f > 0.9 ? theme.palette.statusColors.error
    : f > 0.75 ? theme.palette.statusColors.warn
    : theme.palette.primary.main;
  return (
    <Box sx={{ height: 8, borderRadius: 999, bgcolor: 'surface.border', overflow: 'hidden' }}>
      <Box sx={{ height: '100%', width: `${f * 100}%`, bgcolor: color, transition: 'width 0.4s' }} />
    </Box>
  );
};

const PanelTitle: React.FC<{ children: React.ReactNode }> = ({ children }) => (
  <Typography variant="subtitle2" sx={{ mb: 1.5, fontWeight: 700 }}>{children}</Typography>
);

const SystemDebugDialog: React.FC<Props> = ({ open, onClose, onDownloadSupport }) => {
  const [samples, setSamples] = useState<Sample[]>([]);
  const [stats, setStats] = useState<SystemStats | null>(null);
  const [services, setServices] = useState<ServicesSnapshot | null>(null);
  const [diag, setDiag] = useState<DiagnosticsSnapshot | null>(null);
  const [storage, setStorage] = useState<StorageInfo | null>(null);
  const [info, setInfo] = useState<SystemInfo | null>(null);
  const [logs, setLogs] = useState<string>('');
  const [logsLoading, setLogsLoading] = useState(false);
  const theme = useTheme();

  const logBoxRef = useRef<HTMLDivElement | null>(null);

  const fetchLogs = useCallback(async () => {
    setLogsLoading(true);
    try {
      const res = await apiFetch('/api/system/logs?lines=500');
      const text = res.ok ? await res.text() : 'Failed to load logs.';
      setLogs(text || '(no log output)');
    } catch {
      setLogs('Failed to load logs.');
    } finally {
      setLogsLoading(false);
    }
  }, []);

  // Poll resource stats once per second while the dialog is open, keeping a
  // rolling WINDOW-second buffer that drives the graphs and the queue readout.
  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    const poll = async () => {
      const s = await apiJson<SystemStats>('/api/system/stats');
      if (cancelled || !s) return;
      setStats(s);
      setSamples((prev) => {
        const next = [...prev, { t: Date.now(), cpu: s.cpuPercent, mem: s.memPercent }];
        return next.length > WINDOW ? next.slice(next.length - WINDOW) : next;
      });
    };
    poll();
    const id = setInterval(poll, 1000);
    return () => { cancelled = true; clearInterval(id); };
  }, [open]);

  // Services/ports refresh less often; they change rarely.
  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    const poll = async () => {
      const s = await apiJson<ServicesSnapshot>('/api/system/services');
      if (!cancelled && s) setServices(s);
    };
    poll();
    const id = setInterval(poll, 5000);
    return () => { cancelled = true; clearInterval(id); };
  }, [open]);

  // Composite diagnostics (scanner/GPS/DB/connections/recording/reliability) — ~2s.
  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    const poll = async () => {
      const d = await apiJson<DiagnosticsSnapshot>('/api/system/diagnostics');
      if (!cancelled && d) setDiag(d);
    };
    poll();
    const id = setInterval(poll, 2000);
    return () => { cancelled = true; clearInterval(id); };
  }, [open]);

  // Storage refreshes slowly; it's disk-bound to compute.
  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    const poll = async () => {
      const s = await apiJson<StorageInfo>('/api/system/storage');
      if (!cancelled && s) setStorage(s);
    };
    poll();
    const id = setInterval(poll, 15000);
    return () => { cancelled = true; clearInterval(id); };
  }, [open]);

  // Reset the graph buffer, load one-shot build info, and (re)load logs on open.
  useEffect(() => {
    if (!open) return;
    setSamples([]);
    setStats(null);
    apiJson<SystemInfo>('/api/system/info').then((i) => { if (i) setInfo(i); });
    fetchLogs();
  }, [open, fetchLogs]);

  // Keep the log view pinned to the newest lines when they update.
  useEffect(() => {
    const el = logBoxRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [logs]);

  const queue = stats?.transcription;
  const cpuText = stats ? `${stats.cpuPercent.toFixed(0)}%` : '—';
  const memText = stats
    ? `${stats.memPercent.toFixed(0)}%  (${stats.memUsedMb}/${stats.memTotalMb} MB)`
    : '—';

  const cpuColor = theme.palette.primary.main;
  const memColor = theme.palette.statusColors.info;

  const sortedPorts = useMemo(() => services?.ports ?? [], [services]);

  return (
    <FormDialog
      open={open}
      onClose={onClose}
      title="System Debug"
      icon={<BugReportIcon />}
      maxWidth="lg"
      actions={(
        <>
          <Button
            onClick={onDownloadSupport}
            variant="contained"
            color="primary"
            startIcon={<SupportAgentIcon />}
          >
            Download Support Package
          </Button>
          <Button onClick={onClose} color="inherit">Close</Button>
        </>
      )}
    >
      <Grid container spacing={2}>
        {/* Resource graphs */}
        <Grid size={{ xs: 12, md: 6 }}>
          <Paper variant="outlined" sx={{ p: 2, bgcolor: 'surface.base', height: '100%' }}>
            <Typography variant="subtitle2" sx={{ mb: 1.5, fontWeight: 700 }}>Resources · last {WINDOW}s</Typography>
            <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
              <ResourceGraph label="CPU" color={cpuColor} samples={samples} value={(s) => s.cpu} currentText={cpuText} />
              <ResourceGraph label="Memory" color={memColor} samples={samples} value={(s) => s.mem} currentText={memText} />
            </Box>
          </Paper>
        </Grid>

        {/* Services + transcription queue */}
        <Grid size={{ xs: 12, md: 6 }}>
          <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2, height: '100%' }}>
            <Paper variant="outlined" sx={{ p: 2, bgcolor: 'surface.base' }}>
              <Typography variant="subtitle2" sx={{ mb: 1.5, fontWeight: 700 }}>Transcription Queue</Typography>
              {queue ? (
                <Box sx={{ display: 'flex', gap: 3 }}>
                  <Box>
                    <Typography variant="h4" sx={{ fontFamily: MONO, fontWeight: 700, lineHeight: 1 }}>{queue.queued}</Typography>
                    <Typography variant="caption" color="text.secondary">queued clips</Typography>
                  </Box>
                  <Box>
                    <Typography variant="h4" sx={{ fontFamily: MONO, fontWeight: 700, lineHeight: 1 }}>{queue.workers}</Typography>
                    <Typography variant="caption" color="text.secondary">active workers</Typography>
                  </Box>
                </Box>
              ) : (
                <CircularProgress size={18} />
              )}
            </Paper>

            <Paper variant="outlined" sx={{ p: 2, bgcolor: 'surface.base' }}>
              <Typography variant="subtitle2" sx={{ mb: 1.5, fontWeight: 700 }}>Services</Typography>
              {services ? (
                <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1 }}>
                  {services.services.map((svc) => (
                    <Box key={svc.name} sx={{ display: 'flex', alignItems: 'center', gap: 1.5 }}>
                      <StatusChip label={svc.state} tone={serviceTone(svc.state)} variant="filled" sx={{ fontSize: 10, minWidth: 96 }} />
                      <Tooltip title={svc.detail}>
                        <Typography variant="body2" sx={{ fontFamily: MONO, fontWeight: 700 }}>{svc.name}</Typography>
                      </Tooltip>
                    </Box>
                  ))}
                </Box>
              ) : (
                <CircularProgress size={18} />
              )}
            </Paper>

            <Paper variant="outlined" sx={{ p: 2, bgcolor: 'surface.base', flexGrow: 1 }}>
              <Typography variant="subtitle2" sx={{ mb: 1.5, fontWeight: 700 }}>Listening Ports</Typography>
              {services ? (
                sortedPorts.length ? (
                  <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1 }}>
                    {sortedPorts.map((p) => (
                      <Tooltip key={`${p.protocol}-${p.port}`} title={p.process || 'unknown process'}>
                        <Box sx={{
                          px: 1.25, py: 0.5, borderRadius: 1, border: '1px solid', borderColor: 'surface.border',
                          fontFamily: MONO, fontSize: 12, display: 'flex', gap: 0.75, alignItems: 'baseline',
                        }}>
                          <Box component="span" sx={{ color: 'primary.main', fontWeight: 700 }}>{p.port}</Box>
                          <Box component="span" sx={{ color: 'text.secondary' }}>{p.process || p.protocol}</Box>
                        </Box>
                      </Tooltip>
                    ))}
                  </Box>
                ) : (
                  <Typography variant="caption" color="text.secondary">No listening ports detected.</Typography>
                )
              ) : (
                <CircularProgress size={18} />
              )}
            </Paper>
          </Box>
        </Grid>

        {/* SDR / Scanner */}
        <Grid size={{ xs: 12, md: 4 }}>
          <Paper variant="outlined" sx={{ p: 2, bgcolor: 'surface.base', height: '100%' }}>
            <PanelTitle>SDR / Scanner</PanelTitle>
            {diag ? (
              <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0.75 }}>
                <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 0.5 }}>
                  <StatusChip
                    label={diag.scanner.hardwareConnected ? 'SDR READY' : 'DISCONNECTED'}
                    tone={diag.scanner.hardwareConnected ? 'success' : 'error'}
                    variant="filled"
                    sx={{ fontSize: 10 }}
                  />
                  <StatusChip label={diag.scanner.status} tone="info" sx={{ fontSize: 10 }} />
                </Box>
                <StatRow label="Frequency" value={diag.scanner.frequency != null ? `${diag.scanner.frequency.toFixed(4)} MHz` : '—'} />
                <StatRow label="Signal" value={diag.scanner.signalDb != null ? `${diag.scanner.signalDb.toFixed(1)} dB` : '—'} />
                <Box sx={{ my: 0.5 }}><UsageBar fraction={diag.scanner.signalStrength / 100} /></Box>
                <StatRow label="Gain" value={diag.scanner.gain != null ? (diag.scanner.gain === 0 ? 'AUTO' : `${diag.scanner.gain} dB`) : '—'} />
                <StatRow label="Squelch" value={diag.scanner.squelch != null ? `${diag.scanner.squelch.toFixed(1)} dB` : '—'} />
                <StatRow label="Audio" value={diag.scanner.audioStreaming ? 'streaming' : 'idle'} />
                <StatRow label="Throughput" value={`${diag.radio.throughputKbps.toFixed(1)} KB/s`} />
                <StatRow label="SDR restarts" value={diag.radio.restartCount} title="Capture watchdog restarts since startup" />
              </Box>
            ) : <CircularProgress size={18} />}
          </Paper>
        </Grid>

        {/* GPS */}
        <Grid size={{ xs: 12, md: 4 }}>
          <Paper variant="outlined" sx={{ p: 2, bgcolor: 'surface.base', height: '100%' }}>
            <PanelTitle>GPS</PanelTitle>
            {diag ? (
              <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0.75 }}>
                <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 0.5 }}>
                  <StatusChip
                    label={diag.gps.gpsdConnected ? 'GPSD UP' : 'GPSD DOWN'}
                    tone={diag.gps.gpsdConnected ? 'success' : 'muted'}
                    variant="filled"
                    sx={{ fontSize: 10 }}
                  />
                  <StatusChip
                    label={FIX_LABEL[diag.gps.location?.fix ?? 0] ?? 'No fix'}
                    tone={(diag.gps.location?.fix ?? 0) >= 2 ? 'success' : 'warn'}
                    sx={{ fontSize: 10 }}
                  />
                </Box>
                <StatRow label="Satellites" value={diag.gps.location ? `${diag.gps.location.sats}${diag.gps.location.satsVisible ? ` / ${diag.gps.location.satsVisible}` : ''}` : '—'} />
                <StatRow label="HDOP" value={diag.gps.location?.hdop != null ? diag.gps.location.hdop.toFixed(1) : '—'} />
                <StatRow label="Lat / Lon" value={diag.gps.location && diag.gps.location.fix >= 2 ? `${diag.gps.location.lat.toFixed(4)}, ${diag.gps.location.lon.toFixed(4)}` : '—'} />
                <StatRow label="Altitude" value={diag.gps.location && diag.gps.location.fix >= 3 ? `${Math.round(diag.gps.location.alt * 3.28084)} ft` : '—'} />
                <StatRow label="Speed" value={diag.gps.location && diag.gps.location.fix >= 2 ? `${Math.round(diag.gps.location.speed * 2.23694)} mph` : '—'} />
                <StatRow label="Last fix" value={fmtAge(diag.gps.secondsSinceFix)} />
              </Box>
            ) : <CircularProgress size={18} />}
          </Paper>
        </Grid>

        {/* Recordings / DB + Connections */}
        <Grid size={{ xs: 12, md: 4 }}>
          <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2, height: '100%' }}>
            <Paper variant="outlined" sx={{ p: 2, bgcolor: 'surface.base' }}>
              <PanelTitle>Recordings &amp; DB</PanelTitle>
              {diag ? (
                <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0.75 }}>
                  <StatRow label="Total recordings" value={diag.database.totalRecordings.toLocaleString()} />
                  <StatRow label="Transcribed" value={diag.database.transcribed.toLocaleString()} />
                  <StatRow label="Pending" value={diag.database.pending.toLocaleString()} />
                  <StatRow label="In-flight" value={diag.recording.activeCount} title={diag.recording.activeIds.join('\n') || undefined} />
                  <StatRow label="Oldest" value={fmtTs(diag.database.oldestUtc)} />
                  <StatRow label="Newest" value={fmtTs(diag.database.newestUtc)} />
                </Box>
              ) : <CircularProgress size={18} />}
            </Paper>

            <Paper variant="outlined" sx={{ p: 2, bgcolor: 'surface.base' }}>
              <PanelTitle>Live Connections</PanelTitle>
              {diag ? (
                <Box sx={{ display: 'flex', gap: 3 }}>
                  <Box>
                    <Typography variant="h4" sx={{ fontFamily: MONO, fontWeight: 700, lineHeight: 1 }}>{diag.connections.controlClients}</Typography>
                    <Typography variant="caption" color="text.secondary">control</Typography>
                  </Box>
                  <Box>
                    <Typography variant="h4" sx={{ fontFamily: MONO, fontWeight: 700, lineHeight: 1 }}>{diag.connections.audioClients}</Typography>
                    <Typography variant="caption" color="text.secondary">audio</Typography>
                  </Box>
                </Box>
              ) : <CircularProgress size={18} />}
            </Paper>

            <Paper variant="outlined" sx={{ p: 2, bgcolor: 'surface.base', flexGrow: 1 }}>
              <PanelTitle>Storage</PanelTitle>
              {storage ? (
                <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0.75 }}>
                  <UsageBar fraction={storage.diskTotalBytes ? (storage.diskTotalBytes - storage.diskFreeBytes) / storage.diskTotalBytes : 0} />
                  <StatRow label="Disk free" value={`${fmtBytes(storage.diskFreeBytes)} / ${fmtBytes(storage.diskTotalBytes)}`} />
                  <StatRow label="Recordings" value={`${fmtBytes(storage.recordingsBytes)} · ${storage.recordingsCount.toLocaleString()}`} />
                  <StatRow label="Database" value={fmtBytes(storage.databaseBytes)} />
                </Box>
              ) : <CircularProgress size={18} />}
            </Paper>
          </Box>
        </Grid>

        {/* Health footer: uptime, version, model status, cleanup */}
        <Grid size={12}>
          <Paper variant="outlined" sx={{ p: 2, bgcolor: 'surface.base' }}>
            <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: { xs: 2, md: 4 }, alignItems: 'center' }}>
              <StatRow label="Uptime" value={diag?.uptime ?? '—'} />
              <StatRow label="Version" value={info?.Version ?? '—'} />
              <StatRow label="Commit" value={info?.Commit ? info.Commit.slice(0, 8) : '—'} title={info?.Commit ?? undefined} />
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                <Typography variant="caption" color="text.secondary">Model</Typography>
                <StatusChip label={diag?.transcriptionModelStatus ?? 'unknown'} tone={modelStatusTone(diag?.transcriptionModelStatus)} sx={{ fontSize: 10 }} />
              </Box>
              <StatRow
                label="Cleanup"
                value={diag?.cleanup.lastRunUtc
                  ? `${fmtBytes(diag.cleanup.lastFreeBytes ?? 0)} free · ${diag.cleanup.totalPurged} purged`
                  : 'not run yet'}
                title={diag?.cleanup.lastRunUtc ? `Last run ${fmtTs(diag.cleanup.lastRunUtc)}` : undefined}
              />
            </Box>
          </Paper>
        </Grid>

        {/* Logs */}
        <Grid size={12}>
          <Paper variant="outlined" sx={{ p: 2, bgcolor: 'surface.base' }}>
            <Box sx={{ display: 'flex', alignItems: 'center', mb: 1.5 }}>
              <Typography variant="subtitle2" sx={{ flexGrow: 1, fontWeight: 700 }}>
                OpenScanner Logs <Box component="span" sx={{ color: 'text.secondary', fontWeight: 400 }}>(systemd · journalctl -u openscanner)</Box>
              </Typography>
              <Button size="small" startIcon={<RefreshIcon />} onClick={fetchLogs} disabled={logsLoading}>
                Refresh
              </Button>
            </Box>
            <Box
              ref={logBoxRef}
              sx={{
                height: 260,
                overflow: 'auto',
                bgcolor: 'background.default',
                border: '1px solid',
                borderColor: 'surface.border',
                borderRadius: 1,
                p: 1.5,
                fontFamily: MONO,
                fontSize: 11.5,
                lineHeight: 1.5,
                whiteSpace: 'pre-wrap',
                wordBreak: 'break-word',
                color: 'text.secondary',
              }}
            >
              {logsLoading && !logs ? <CircularProgress size={18} /> : logs}
            </Box>
          </Paper>
        </Grid>
      </Grid>
    </FormDialog>
  );
};

export default SystemDebugDialog;
