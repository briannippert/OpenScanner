import { memo, useEffect, useState } from 'react';
import { Box, Paper, Typography, CircularProgress } from '@mui/material';

interface Props {
  isConnected: boolean;
  /** Epoch ms of the next reconnect attempt, or null when none is scheduled. */
  reconnectAt: number | null;
}

/**
 * Full-screen blocker shown while the control socket is down. Owns its own countdown
 * timer so the once-a-second tick re-renders this overlay rather than the whole app.
 */
const ConnectionOverlay = ({ isConnected, reconnectAt }: Props) => {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (isConnected || reconnectAt == null) return;
    const id = setInterval(() => setNow(Date.now()), 250);
    return () => clearInterval(id);
  }, [isConnected, reconnectAt]);

  if (isConnected) return null;

  const seconds = reconnectAt != null ? Math.max(0, Math.ceil((reconnectAt - now) / 1000)) : null;

  return (
    <Box sx={{
      position: 'absolute', inset: 0, zIndex: 9999,
      bgcolor: 'rgba(0,0,0,0.8)', backdropFilter: 'blur(4px)',
      display: 'flex', alignItems: 'center', justifyContent: 'center',
    }}>
      <Paper role="alert" sx={{ p: 4, textAlign: 'center', borderColor: 'error.main', minWidth: 300 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 1.5, mb: 1 }}>
          <CircularProgress size={20} color="error" />
          <Typography variant="h5" color="error" fontWeight={700}>CONNECTION LOST</Typography>
        </Box>
        <Typography color="text.secondary">
          {seconds != null && seconds > 0
            ? `Reconnecting to OpenScanner server in ${seconds}s…`
            : 'Reconnecting to OpenScanner server…'}
        </Typography>
      </Paper>
    </Box>
  );
};

export default memo(ConnectionOverlay);
