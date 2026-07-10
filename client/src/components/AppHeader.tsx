import React, { useState } from 'react';
import {
  AppBar, Toolbar, Typography, Box, IconButton, Slider, Tooltip, Menu, MenuItem, ListItemIcon, ListItemText,
} from '@mui/material';
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import PauseIcon from '@mui/icons-material/Pause';
import LocationOnIcon from '@mui/icons-material/LocationOn';
import SpeedIcon from '@mui/icons-material/Speed';
import SatelliteAltIcon from '@mui/icons-material/SatelliteAlt';
import AccessTimeIcon from '@mui/icons-material/AccessTime';
import UsbIcon from '@mui/icons-material/Usb';
import UsbOffIcon from '@mui/icons-material/UsbOff';
import NotificationsActiveIcon from '@mui/icons-material/NotificationsActive';
import FullscreenIcon from '@mui/icons-material/Fullscreen';
import FullscreenExitIcon from '@mui/icons-material/FullscreenExit';
import SupportAgentIcon from '@mui/icons-material/SupportAgent';
import SettingsIcon from '@mui/icons-material/Settings';
import VolumeUpIcon from '@mui/icons-material/VolumeUp';
import MonitorIcon from '@mui/icons-material/Monitor';
import MoreVertIcon from '@mui/icons-material/MoreVert';
import type { ScannerState } from '../types';
import StatusChip from './common/StatusChip';

interface Props {
  scannerState: ScannerState;
  currentTime: Date;
  volume: number;
  onVolumeChange: (v: number) => void;
  manualHold?: number;
  onResume: () => void;
  isFullscreen: boolean;
  onToggleFullscreen: () => void;
  onOpenFireTones: () => void;
  onOpenSettings: () => void;
  onOpenDebug: () => void;
  onDownloadSupport: () => void;
}

const MONO = '"Roboto Mono", ui-monospace, SFMono-Regular, Menlo, monospace';
const gpsMono = { fontFamily: MONO, color: 'text.primary' } as const;

const AppHeader: React.FC<Props> = ({
  scannerState, currentTime, volume, onVolumeChange, manualHold, onResume,
  isFullscreen, onToggleFullscreen, onOpenFireTones, onOpenSettings, onOpenDebug, onDownloadSupport,
}) => {
  const [menuAnchor, setMenuAnchor] = useState<null | HTMLElement>(null);
  const gps = scannerState.gps;
  const hasFix = gps?.time && gps.fix >= 2;

  // The toolbar actions, shared between the desktop icon row and the mobile menu.
  const actions = [
    { label: 'Fire Tone Outs', icon: <NotificationsActiveIcon />, onClick: onOpenFireTones },
    { label: 'Settings', icon: <SettingsIcon />, onClick: onOpenSettings },
    { label: isFullscreen ? 'Exit Fullscreen' : 'Fullscreen', icon: isFullscreen ? <FullscreenExitIcon /> : <FullscreenIcon />, onClick: onToggleFullscreen },
    { label: 'RF Spectrum Debug', icon: <MonitorIcon />, onClick: onOpenDebug },
    { label: 'Download Support Package', icon: <SupportAgentIcon />, onClick: onDownloadSupport },
  ];

  return (
    <AppBar position="static">
      <Toolbar variant="dense">
        <Typography
          variant="h6"
          component="div"
          sx={{
            flexGrow: 1, color: 'primary.main', fontWeight: 900,
            fontFamily: MONO,
            letterSpacing: { xs: 1, sm: 3 }, fontSize: { xs: '1.1rem', sm: '1.25rem' },
          }}
        >
          OPENSCANNER
        </Typography>

        <Box sx={{ mr: 2, display: { xs: 'none', sm: 'block' } }}>
          <Tooltip title={scannerState.isHardwareConnected ? `${scannerState.deviceName || 'RTL-SDR Device'} (${scannerState.devicePort || 'USB'})` : 'No Device Detected'}>
            <span>
              <StatusChip
                icon={scannerState.isHardwareConnected ? <UsbIcon /> : <UsbOffIcon />}
                label={scannerState.isHardwareConnected ? 'SDR READY' : 'SDR DISCONNECTED'}
                tone={scannerState.isHardwareConnected ? 'success' : 'error'}
                sx={{ fontSize: 10 }}
              />
            </span>
          </Tooltip>
        </Box>

        {/* Clock / GPS time */}
        <Box sx={{
          mr: { xs: 1, sm: 3 }, px: { xs: 1, sm: 2 }, py: 0.5, display: 'flex', alignItems: 'center', gap: 1,
          bgcolor: 'surface.base', borderRadius: 1.5, border: '1px solid', borderColor: 'surface.border',
        }}>
          {hasFix
            ? <SatelliteAltIcon sx={{ fontSize: 16, color: 'primary.main' }} />
            : <AccessTimeIcon sx={{ fontSize: 16, color: 'text.secondary' }} />}
          <Typography variant="caption" sx={{ ...gpsMono, color: 'primary.main', fontWeight: 700, fontSize: { xs: 12, sm: 14 } }}>
            {hasFix
              ? new Date(gps!.time).toLocaleTimeString([], { hour12: false })
              : currentTime.toLocaleTimeString([], { hour12: false })}
          </Typography>
        </Box>

        {gps && (
          <Box sx={{
            display: { xs: 'none', md: 'flex' }, alignItems: 'center', gap: 2, mr: 3, px: 2, py: 0.5,
            bgcolor: 'surface.base', borderRadius: 1.5, border: '1px solid', borderColor: 'surface.border',
          }}>
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
              <SatelliteAltIcon sx={{ fontSize: 16, color: (gps.satsVisible || 0) > 0 ? 'primary.main' : 'text.disabled' }} />
              <Typography variant="caption" sx={gpsMono}>
                {gps.sats}{gps.satsVisible ? `/${gps.satsVisible}` : ''} SATS
              </Typography>
            </Box>
            {gps.fix < 2 ? (
              <Typography variant="caption" sx={{ color: 'warning.main', fontWeight: 700, fontSize: 10 }}>NO FIX</Typography>
            ) : (
              <>
                <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
                  <LocationOnIcon sx={{ fontSize: 16, color: 'primary.main' }} />
                  <Typography variant="caption" sx={gpsMono}>{gps.lat.toFixed(4)}, {gps.lon.toFixed(4)}</Typography>
                </Box>
                <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
                  <Typography variant="caption" sx={{ color: 'text.secondary', fontSize: 10 }}>ALT</Typography>
                  <Typography variant="caption" sx={gpsMono}>{Math.round(gps.alt * 3.28084)}ft</Typography>
                </Box>
                <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
                  <SpeedIcon sx={{ fontSize: 16, color: 'primary.main' }} />
                  <Typography variant="caption" sx={gpsMono}>{Math.round(gps.speed * 2.23694)} mph</Typography>
                </Box>
              </>
            )}
          </Box>
        )}

        {manualHold && (
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
            <StatusChip label="HOLD" tone="warn" variant="filled" icon={<PauseIcon />} sx={{ display: { xs: 'none', sm: 'flex' } }} />
            <StatusChip label="RESUME" tone="live" variant="filled" icon={<PlayArrowIcon />} onClick={onResume} />
          </Box>
        )}

        <Box sx={{ ml: 2, display: 'flex', alignItems: 'center', gap: 2, width: { xs: 80, sm: 120, md: 150 } }}>
          <VolumeUpIcon sx={{ color: 'text.secondary', fontSize: 20 }} />
          <Slider
            size="small"
            value={volume}
            min={0}
            max={1}
            step={0.01}
            onChange={(_, value) => onVolumeChange(value as number)}
            aria-label="Volume"
            sx={{
              color: 'primary.main',
              '& .MuiSlider-thumb': { width: 12, height: 12 },
              '& .MuiSlider-rail': { opacity: 0.3 },
            }}
          />
        </Box>

        {/* Desktop: action icons grouped into a single toolbar pill */}
        <Box
          sx={{
            display: { xs: 'none', sm: 'flex' },
            alignItems: 'center',
            gap: 0.25,
            ml: 2,
            px: 0.5,
            bgcolor: 'surface.base',
            border: '1px solid',
            borderColor: 'surface.border',
            borderRadius: 999,
          }}
        >
          {actions.map((a) => (
            <Tooltip title={a.label} key={a.label}>
              <IconButton color="inherit" size="small" onClick={a.onClick} aria-label={a.label}>
                {a.icon}
              </IconButton>
            </Tooltip>
          ))}
        </Box>

        {/* Mobile: overflow menu so these actions remain reachable */}
        <Box sx={{ display: { xs: 'flex', sm: 'none' } }}>
          <IconButton color="inherit" aria-label="More actions" onClick={(e) => setMenuAnchor(e.currentTarget)}>
            <MoreVertIcon />
          </IconButton>
          <Menu anchorEl={menuAnchor} open={!!menuAnchor} onClose={() => setMenuAnchor(null)}>
            {actions.map((a) => (
              <MenuItem
                key={a.label}
                onClick={() => { setMenuAnchor(null); a.onClick(); }}
              >
                <ListItemIcon sx={{ color: 'text.secondary' }}>{a.icon}</ListItemIcon>
                <ListItemText>{a.label}</ListItemText>
              </MenuItem>
            ))}
          </Menu>
        </Box>
      </Toolbar>
    </AppBar>
  );
};

export default AppHeader;
