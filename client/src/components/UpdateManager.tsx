import { useCallback, useEffect, useRef, useState } from 'react';
import { Box, Button, Typography, Alert, CircularProgress, Chip, Link } from '@mui/material';
import SystemUpdateIcon from '@mui/icons-material/SystemUpdate';
import CheckCircleIcon from '@mui/icons-material/CheckCircle';
import RefreshIcon from '@mui/icons-material/Refresh';
import FormDialog from './common/FormDialog';
import { apiFetch, apiJson } from './common/apiBase';
import type { UpdateStatus, UpdateState } from '../types';

interface Props {
  open: boolean;
  onClose: () => void;
  /** Live update console lines, accumulated from the control WebSocket. */
  log: string[];
  /** Live update state from the control WebSocket. */
  state: UpdateState;
  /** Seed/replace the shared console (snapshot on open, clear on start). */
  onSeed: (lines: string[], state: UpdateState) => void;
}

const MONO = '"Roboto Mono", ui-monospace, SFMono-Regular, Menlo, monospace';

const shortCommit = (c?: string) => (c ? c.slice(0, 8) : '');

/** "3 minutes ago" for the last-checked caption. The server only checks every 30
 *  minutes, so minute precision is plenty. */
const sinceText = (iso?: string): string => {
  if (!iso) return 'never';
  const secs = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
  if (!Number.isFinite(secs) || secs < 0) return 'just now';
  if (secs < 60) return 'just now';
  const mins = Math.floor(secs / 60);
  if (mins < 60) return `${mins} minute${mins === 1 ? '' : 's'} ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours} hour${hours === 1 ? '' : 's'} ago`;
  const days = Math.floor(hours / 24);
  return `${days} day${days === 1 ? '' : 's'} ago`;
};

const UpdateManager: React.FC<Props> = ({ open, onClose, log, state, onSeed }) => {
  const [status, setStatus] = useState<UpdateStatus | null>(null);
  const [restarted, setRestarted] = useState(false);
  const [checking, setChecking] = useState(false);
  const logBoxRef = useRef<HTMLDivElement>(null);

  // Seed from the authoritative snapshot whenever the dialog opens.
  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    (async () => {
      const s = await apiJson<UpdateStatus>('/api/update/status');
      if (cancelled || !s) return;
      setStatus(s);
      setRestarted(false);
      onSeed(s.log ?? [], s.state);
    })();
    return () => { cancelled = true; };
  }, [open, onSeed]);

  // Auto-scroll the console to the newest line.
  useEffect(() => {
    const el = logBoxRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [log]);

  // Once the build succeeds, poll for the restart to complete (commit changes).
  useEffect(() => {
    if (!open || state !== 'success' || restarted) return;
    const before = status?.currentCommit;
    let cancelled = false;
    const id = setInterval(async () => {
      const info = await apiJson<Record<string, string>>('/api/system/info');
      if (cancelled) return;
      if (info && before && info.Commit && info.Commit !== before) {
        setRestarted(true);
        clearInterval(id);
      }
    }, 3000);
    return () => { cancelled = true; clearInterval(id); };
  }, [open, state, restarted, status?.currentCommit]);

  const start = useCallback(async () => {
    onSeed([], 'updating');
    setRestarted(false);
    await apiFetch('/api/update/start', { method: 'POST' });
  }, [onSeed]);

  // Force a check now rather than waiting out the server's 30-minute poll. The
  // response is the refreshed snapshot, so no follow-up GET is needed; a failed
  // check comes back with `error` set and surfaces in the alert below.
  const check = useCallback(async () => {
    setChecking(true);
    try {
      const s = await apiJson<UpdateStatus>('/api/update/check', { method: 'POST' });
      if (s) setStatus(s);
    } finally {
      setChecking(false);
    }
  }, []);

  const failed = state === 'failed';
  const updating = state === 'updating';
  const available = status?.updateAvailable ?? false;
  const busyChecking = checking || state === 'checking';

  const actionButton = (() => {
    if (state === 'success') {
      return restarted
        ? <Button onClick={onClose} variant="contained" color="success">Done</Button>
        : <Button disabled variant="contained" startIcon={<CircularProgress size={16} />}>Restarting…</Button>;
    }
    if (updating) {
      return <Button disabled variant="contained" startIcon={<CircularProgress size={16} />}>Updating…</Button>;
    }
    if (failed) {
      return <Button onClick={start} variant="contained" color="warning">Retry</Button>;
    }
    return (
      <Button onClick={start} variant="contained" startIcon={<SystemUpdateIcon />} disabled={!available}>
        {available ? `Update to ${status?.latestTag ?? 'latest'}` : 'Up to date'}
      </Button>
    );
  })();

  return (
    <FormDialog
      open={open}
      onClose={onClose}
      title="Software Update"
      icon={<SystemUpdateIcon />}
      maxWidth="md"
      disableClose={updating}
      actions={
        <>
          <Button onClick={onClose} color="inherit" disabled={updating}>Close</Button>
          <Button
            onClick={check}
            disabled={updating || busyChecking || state === 'success'}
            startIcon={busyChecking ? <CircularProgress size={16} /> : <RefreshIcon />}
          >
            {busyChecking ? 'Checking…' : 'Check now'}
          </Button>
          {actionButton}
        </>
      }
    >
      {/* Version summary */}
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, flexWrap: 'wrap', mb: 2 }}>
        <Chip
          size="small"
          label={`current ${status?.currentVersion || '—'} (${shortCommit(status?.currentCommit)})`}
          sx={{ fontFamily: MONO }}
        />
        {status?.latestTag && (
          <>
            <Typography color="text.secondary">→</Typography>
            <Chip
              size="small"
              color={available ? 'warning' : 'default'}
              label={`latest ${status.latestTag}${status.commitsBehind ? ` (${status.commitsBehind} behind)` : ''}`}
              sx={{ fontFamily: MONO }}
            />
          </>
        )}
        <Typography variant="caption" color="text.secondary" sx={{ ml: 'auto' }}>
          {busyChecking ? 'Checking…' : `Checked ${sinceText(status?.lastCheckedUtc)}`}
        </Typography>
        {status?.releaseUrl && (
          <Link href={status.releaseUrl} target="_blank" rel="noopener" variant="caption">
            Release notes
          </Link>
        )}
      </Box>

      {state === 'success' && restarted && (
        <Alert severity="success" icon={<CheckCircleIcon />} sx={{ mb: 2 }}>
          Updated and restarted. You’re now on the latest version.
        </Alert>
      )}
      {state === 'success' && !restarted && (
        <Alert severity="info" sx={{ mb: 2 }}>
          Build succeeded — the service is restarting. This page will reconnect automatically.
        </Alert>
      )}
      {failed && (
        <Alert severity="error" sx={{ mb: 2 }}>
          {status?.error || 'Update failed. The running version is unchanged — see the log below.'}
        </Alert>
      )}
      {/* A failed *check* deliberately leaves the state alone (a network blip
          shouldn't look like a failed update), so it reports through `error`
          only — without this the Check now button would silently snap back to
          "Up to date". */}
      {!failed && !updating && status?.error && (
        <Alert severity="warning" sx={{ mb: 2 }}>
          {status.error}
        </Alert>
      )}
      {!status?.latestTag && !status?.error && !updating && state !== 'success' && (
        <Alert severity="info" sx={{ mb: 2 }}>
          Checking for the latest release… If this persists, the server may be offline from GitHub.
        </Alert>
      )}

      {/* Live console */}
      <Box
        ref={logBoxRef}
        sx={{
          fontFamily: MONO,
          fontSize: 12,
          lineHeight: 1.5,
          whiteSpace: 'pre-wrap',
          wordBreak: 'break-word',
          bgcolor: '#0a0a0a',
          color: failed ? 'error.light' : 'grey.400',
          p: 1.5,
          borderRadius: 1,
          height: 340,
          overflowY: 'auto',
          border: '1px solid',
          borderColor: failed ? 'error.main' : 'surface.border',
        }}
      >
        {log.length ? log.join('\n') : <Typography component="span" sx={{ color: 'text.disabled', fontSize: 12 }}>No output yet.</Typography>}
      </Box>
    </FormDialog>
  );
};

export default UpdateManager;
