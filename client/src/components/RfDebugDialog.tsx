import React from 'react';
import { Box, Typography, TextField, Slider, Button, Paper } from '@mui/material';
import MonitorIcon from '@mui/icons-material/Monitor';
import type { ScannerState } from '../types';
import FormDialog from './common/FormDialog';
import StatusChip from './common/StatusChip';
import RfWaterfallDebug from './RfWaterfallDebug';

interface Props {
  open: boolean;
  onClose: () => void;
  scannerState: ScannerState;
  debugFreq: string;
  onDebugFreqChange: (v: string) => void;
  debugGain: number;
  onDebugGainChange: (v: number) => void;
  onTune: () => void;
}

const RfDebugDialog: React.FC<Props> = ({
  open, onClose, scannerState, debugFreq, onDebugFreqChange, debugGain, onDebugGainChange, onTune,
}) => {
  const tuneDisabled =
    scannerState.status === 'DEBUG' &&
    scannerState.currentFrequency === Number(debugFreq) &&
    scannerState.gain === debugGain;

  return (
    <FormDialog
      open={open}
      onClose={onClose}
      title="RF Spectrum Debug"
      icon={<MonitorIcon />}
      maxWidth="lg"
      actions={<Button onClick={onClose} color="inherit">Close</Button>}
    >
      <Box sx={{ mb: 3, display: 'flex', gap: 3, alignItems: 'center', flexWrap: 'wrap' }}>
        <TextField
          label="Center Frequency"
          variant="outlined"
          size="small"
          value={debugFreq}
          onChange={(e) => onDebugFreqChange(e.target.value)}
          sx={{ width: 180 }}
        />
        <Box sx={{ width: 180 }}>
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 0.5 }}>
            SDR GAIN: {debugGain === 0 ? 'AUTO' : `${debugGain} dB`}
          </Typography>
          <Slider
            value={debugGain}
            min={0}
            max={50}
            step={1}
            onChange={(_, val) => onDebugGainChange(val as number)}
            valueLabelDisplay="auto"
            valueLabelFormat={(val) => (val === 0 ? 'AUTO' : `${val}dB`)}
            size="small"
            aria-label="SDR gain"
          />
        </Box>
        <Button variant="contained" color="primary" onClick={onTune} disabled={tuneDisabled} sx={{ height: 40, px: 4 }}>
          TUNE
        </Button>
        <Box flexGrow={1} />
        <Paper
          variant="outlined"
          sx={{ px: 2, py: 1, bgcolor: 'surface.base', display: 'flex', alignItems: 'center', gap: 2 }}
        >
          <Typography variant="caption" color="text.secondary">SDR STATUS:</Typography>
          <StatusChip
            label={scannerState.status}
            tone={scannerState.status === 'DEBUG' ? 'success' : 'muted'}
            variant="filled"
            sx={{ fontSize: 10 }}
          />
        </Paper>
      </Box>

      <RfWaterfallDebug data={scannerState.rfSpectrum} height={500} />

      <Box sx={{ mt: 3, p: 2, borderRadius: 1.5, bgcolor: 'surface.base', border: '1px solid', borderColor: 'surface.border' }}>
        <Box sx={{ display: 'flex', gap: 1.5, alignItems: 'center', mb: 1 }}>
          <Typography variant="caption" sx={{ color: 'warning.main', fontWeight: 700, minWidth: 100 }}>SYSTEM NOTE</Typography>
          <Typography variant="caption" color="text.secondary">
            Debug mode requires exclusive hardware access. Scanning and decoding are suspended while active.
          </Typography>
        </Box>
        <Box sx={{ display: 'flex', gap: 1.5, alignItems: 'center' }}>
          <Typography variant="caption" sx={{ color: 'info.main', fontWeight: 700, minWidth: 100 }}>HARDWARE TIP</Typography>
          <Typography variant="caption" color="text.secondary">
            The center spike is a DC offset common in RTL-SDR hardware and does not indicate a real signal.
          </Typography>
        </Box>
      </Box>
    </FormDialog>
  );
};

export default RfDebugDialog;
