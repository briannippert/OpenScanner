import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Dialog, Box, TextField, List, ListItemButton, Typography, InputAdornment } from '@mui/material';
import { alpha } from '@mui/material/styles';
import SearchIcon from '@mui/icons-material/Search';
import RadioIcon from '@mui/icons-material/Radio';
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import SkipNextIcon from '@mui/icons-material/SkipNext';
import SettingsIcon from '@mui/icons-material/Settings';
import NotificationsActiveIcon from '@mui/icons-material/NotificationsActive';
import EditIcon from '@mui/icons-material/Edit';
import MonitorIcon from '@mui/icons-material/Monitor';
import FullscreenIcon from '@mui/icons-material/Fullscreen';
import type { Channel } from '../types';

interface Command {
  id: string;
  label: string;
  hint?: string;
  section: string;
  icon: React.ReactNode;
  keywords: string;
  run: () => void;
}

interface Props {
  open: boolean;
  onClose: () => void;
  channels: Channel[];
  manualHold?: number;
  onHold: (ch: Channel) => void;
  onResume: () => void;
  onSkip: () => void;
  onOpenSettings: () => void;
  onOpenFireTones: () => void;
  onOpenChannels: () => void;
  onOpenDebug: () => void;
  onToggleFullscreen: () => void;
}

const CommandPalette: React.FC<Props> = ({
  open, onClose, channels, manualHold, onHold, onResume, onSkip,
  onOpenSettings, onOpenFireTones, onOpenChannels, onOpenDebug, onToggleFullscreen,
}) => {
  const [query, setQuery] = useState('');
  const [active, setActive] = useState(0);
  const listRef = useRef<HTMLUListElement>(null);

  // Reset query + selection whenever the palette opens/closes — adjust state
  // during render on the changed prop (React's pattern) rather than in an effect.
  const [prevOpen, setPrevOpen] = useState(open);
  if (open !== prevOpen) {
    setPrevOpen(open);
    setQuery('');
    setActive(0);
  }

  const commands = useMemo<Command[]>(() => {
    const run = (fn: () => void) => () => { fn(); onClose(); };
    const channelCmds: Command[] = channels.map(ch => {
      const held = manualHold !== undefined && Math.abs(manualHold - ch.frequency) < 0.0001;
      return {
        id: `ch-${ch.frequency}`,
        label: held ? `Resume from ${ch.alphaTag}` : `Hold ${ch.alphaTag}`,
        hint: `${ch.frequency} MHz`,
        section: 'Channels',
        icon: <RadioIcon fontSize="small" />,
        keywords: `${ch.alphaTag} ${ch.description} ${ch.frequency} hold`,
        run: run(() => onHold(ch)),
      };
    });
    const actionCmds: Command[] = [
      { id: 'resume', label: 'Resume scanning', section: 'Actions', icon: <PlayArrowIcon fontSize="small" />, keywords: 'resume scan run', run: run(onResume) },
      { id: 'skip', label: 'Skip current transmission', section: 'Actions', icon: <SkipNextIcon fontSize="small" />, keywords: 'skip next avoid', run: run(onSkip) },
      { id: 'channels', label: 'Manage channels', section: 'Actions', icon: <EditIcon fontSize="small" />, keywords: 'channels edit manage add', run: run(onOpenChannels) },
      { id: 'firetones', label: 'Fire tone-outs', section: 'Actions', icon: <NotificationsActiveIcon fontSize="small" />, keywords: 'fire tone out alerts', run: run(onOpenFireTones) },
      { id: 'settings', label: 'Open settings', section: 'Actions', icon: <SettingsIcon fontSize="small" />, keywords: 'settings preferences storage', run: run(onOpenSettings) },
      { id: 'debug', label: 'RF spectrum debug', section: 'Actions', icon: <MonitorIcon fontSize="small" />, keywords: 'rf spectrum debug waterfall', run: run(onOpenDebug) },
      { id: 'fullscreen', label: 'Toggle fullscreen', section: 'Actions', icon: <FullscreenIcon fontSize="small" />, keywords: 'fullscreen full screen', run: run(onToggleFullscreen) },
    ];
    return [...actionCmds, ...channelCmds];
  }, [channels, manualHold, onHold, onResume, onSkip, onOpenChannels, onOpenFireTones, onOpenSettings, onOpenDebug, onToggleFullscreen, onClose]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return commands;
    return commands.filter(c => (c.label + ' ' + c.keywords).toLowerCase().includes(q));
  }, [commands, query]);

  // Keep the active item scrolled into view (DOM only, no state).
  useEffect(() => {
    const el = listRef.current?.querySelector(`[data-idx="${active}"]`);
    el?.scrollIntoView({ block: 'nearest' });
  }, [active]);

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive(a => Math.min(a + 1, filtered.length - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(a => Math.max(a - 1, 0)); }
    else if (e.key === 'Enter') { e.preventDefault(); filtered[active]?.run(); }
  };

  let lastSection = '';

  return (
    <Dialog
      open={open}
      onClose={onClose}
      maxWidth="sm"
      fullWidth
      slotProps={{ paper: { sx: { position: 'fixed', top: 80, m: 0, borderRadius: 3 } } }}
    >
      <Box sx={{ p: 1.5, borderBottom: '1px solid', borderColor: 'surface.border' }}>
        <TextField
          autoFocus
          fullWidth
          variant="outlined"
          size="small"
          placeholder="Type a command or channel…"
          value={query}
          onChange={(e) => { setQuery(e.target.value); setActive(0); }}
          onKeyDown={onKeyDown}
          slotProps={{ input: { startAdornment: <InputAdornment position="start"><SearchIcon sx={{ color: 'text.secondary' }} /></InputAdornment> } }}
        />
      </Box>
      <List ref={listRef} dense sx={{ maxHeight: 380, overflowY: 'auto', py: 0.5 }}>
        {filtered.length === 0 && (
          <Typography variant="body2" sx={{ p: 3, textAlign: 'center', color: 'text.secondary' }}>No matching commands</Typography>
        )}
        {filtered.map((c, i) => {
          const showSection = c.section !== lastSection;
          lastSection = c.section;
          return (
            <React.Fragment key={c.id}>
              {showSection && (
                <Typography variant="caption" sx={{ display: 'block', px: 2, pt: 1, pb: 0.5, color: 'text.disabled', fontWeight: 700, letterSpacing: 0.6, textTransform: 'uppercase' }}>
                  {c.section}
                </Typography>
              )}
              <ListItemButton
                data-idx={i}
                selected={i === active}
                onMouseEnter={() => setActive(i)}
                onClick={c.run}
                sx={{ mx: 1, borderRadius: 1.5, gap: 1.5, '&.Mui-selected': { bgcolor: (t) => alpha(t.palette.primary.main, 0.16) } }}
              >
                <Box sx={{ display: 'flex', color: 'primary.main' }}>{c.icon}</Box>
                <Box sx={{ flexGrow: 1, minWidth: 0 }}>
                  <Typography variant="body2" noWrap>{c.label}</Typography>
                </Box>
                {c.hint && (
                  <Typography variant="caption" sx={{ color: 'text.disabled', fontFamily: (t) => t.typography.mono.fontFamily }}>{c.hint}</Typography>
                )}
              </ListItemButton>
            </React.Fragment>
          );
        })}
      </List>
    </Dialog>
  );
};

export default CommandPalette;
