import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Box, Button, Checkbox, Chip, CircularProgress, Divider, Drawer, IconButton, ListItemIcon,
  ListItemText, Menu, MenuItem, Stack, Switch, Tooltip, Typography, useMediaQuery,
} from '@mui/material';
import { useTheme } from '@mui/material/styles';
import MapIcon from '@mui/icons-material/Map';
import TuneIcon from '@mui/icons-material/Tune';
import ListIcon from '@mui/icons-material/FormatListBulleted';
import HeadphonesIcon from '@mui/icons-material/Headphones';
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import StopIcon from '@mui/icons-material/Stop';
import CloseIcon from '@mui/icons-material/Close';
import CallMap, { type LayerMode } from '../components/map/CallMap';
import NowPlayingBar from '../components/NowPlayingBar';
import SegmentedControl from '../components/common/SegmentedControl';
import EmptyState from '../components/common/EmptyState';
import { apiJson } from '../components/common/apiBase';
import { useAudioPipeline } from '../hooks/useAudioPipeline';
import { useLiveLogs } from '../hooks/useLiveLogs';
import {
  CALL_GROUPS, GROUP_ORDER, callTypeLabel, filterMapCalls, groupOf, mapCallFromLog, typesInGroup,
  type CallGroup, type ServiceFilter,
} from '../lib/callTypes';
import type { AiCapabilities, CallLog, MapCall } from '../types';

type Range = '1h' | '6h' | '24h' | '7d' | '30d';

const RANGE_MS: Record<Range, number> = {
  '1h': 3_600_000, '6h': 6 * 3_600_000, '24h': 24 * 3_600_000, '7d': 7 * 86_400_000, '30d': 30 * 86_400_000,
};

/** A row of controls: wraps on desktop, scrolls sideways in one line on phones. */
const scrollRow = {
  display: 'flex',
  alignItems: 'center',
  gap: 1,
  flexWrap: { xs: 'nowrap', md: 'wrap' },
  overflowX: { xs: 'auto', md: 'visible' },
  flexBasis: { xs: '100%', md: 'auto' },
  flexGrow: 1,
  minWidth: 0,
  scrollbarWidth: 'none',
  '& > *': { flexShrink: 0 },
} as const;

/** How long a live call's marker pulses. */
const FRESH_MS = 10_000;
/** Rows rendered in the side list; the map shows all of them. */
const LIST_LIMIT = 300;

const relTime = (iso: string, now: number) => {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return '';
  const s = Math.max(0, Math.round((now - t) / 1000));
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.floor(s / 60)}m ago`;
  if (s < 86_400) return `${Math.floor(s / 3600)}h ago`;
  return new Date(t).toLocaleDateString([], { month: 'short', day: 'numeric' });
};

/** The group's marker shape, small, for chips, the list and the legend. */
const GroupGlyph: React.FC<{ group: CallGroup; size?: number }> = ({ group, size = 10 }) => {
  const g = CALL_GROUPS[group];
  return (
    <Box
      component="span"
      aria-hidden
      sx={{
        display: 'inline-block',
        flexShrink: 0,
        width: size,
        height: size,
        bgcolor: g.color,
        borderRadius: g.shape === 'square' ? '2px' : g.shape === 'diamond' ? '1px' : '50%',
        transform: g.shape === 'diamond' ? 'rotate(45deg) scale(0.85)' : g.shape === 'dot' ? 'scale(0.8)' : undefined,
      }}
    />
  );
};

/**
 * Full-page incident map, opened in its own tab from the dashboard. Shows calls that the
 * AI analysis located, live and historical, as markers and/or a heat map, filterable by
 * call type and service. Plays recordings in a mini player; there are no radio controls.
 */
const MapPage: React.FC = () => {
  const theme = useTheme();
  const isDesktop = useMediaQuery(theme.breakpoints.up('md'));

  const [caps, setCaps] = useState<AiCapabilities | null>(null);
  const [range, setRange] = useState<Range>('24h');
  const [calls, setCalls] = useState<MapCall[]>([]);
  // The range whose calls are loaded; loading while it differs from the selected one.
  const [loadedRange, setLoadedRange] = useState<Range | null>(null);
  const [loadError, setLoadError] = useState(false);
  const [fitKey, setFitKey] = useState(0);
  const [layers, setLayers] = useState<LayerMode>('markers');
  const [service, setService] = useState<ServiceFilter>('all');
  // Call types hidden by the filter. Empty = everything shown.
  const [hiddenTypes, setHiddenTypes] = useState<ReadonlySet<string>>(new Set());
  const [selectedId, setSelectedId] = useState<string | null>(null);
  // Live arrivals → when they arrived. Drives the marker blip, the callout and the list highlight.
  const [fresh, setFresh] = useState<ReadonlyMap<string, number>>(new Map());
  const [follow, setFollow] = useState(false);
  const [listenLive, setListenLive] = useState(false);
  const [listOpen, setListOpen] = useState(false);
  const [typeMenu, setTypeMenu] = useState<HTMLElement | null>(null);
  const [now, setNow] = useState(() => Date.now());

  const volume = useMemo(() => {
    try {
      const v = parseFloat(localStorage.getItem('scannerVolume') ?? '');
      return Number.isFinite(v) ? v : 1;
    } catch {
      return 1;
    }
  }, []);
  const audio = useAudioPipeline(volume, { live: listenLive });

  useEffect(() => { document.title = 'Call Map · OpenScanner'; }, []);
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 30_000);
    return () => clearInterval(id);
  }, []);

  useEffect(() => {
    void apiJson<AiCapabilities>('/api/ai/capabilities').then(c => setCaps(c ?? { supported: false, enabled: false }));
  }, []);

  // Historical calls for the selected range. Filtering by type/service is client-side so
  // it is instant and applies to live calls the same way.
  useEffect(() => {
    let cancelled = false;
    const since = new Date(Date.now() - RANGE_MS[range]).toISOString();
    void apiJson<MapCall[]>(`/api/ai/calls?since=${encodeURIComponent(since)}&limit=5000`).then(result => {
      if (cancelled) return;
      setLoadError(result == null);
      setCalls(result ?? []);
      setLoadedRange(range);
      setFitKey(k => k + 1);
    });
    return () => { cancelled = true; };
  }, [range]);

  const followRef = useRef(follow);
  useEffect(() => { followRef.current = follow; }, [follow]);

  // Live calls: a log is re-broadcast once its AI analysis is in; add it if it has a location.
  const onLiveLog = useCallback((log: CallLog) => {
    const call = mapCallFromLog(log);
    if (!call) return;
    setCalls(prev => [call, ...prev.filter(c => c.id !== call.id)]);
    // A re-broadcast of a call that is already fresh keeps its original arrival time.
    setFresh(prev => prev.has(call.id) ? prev : new Map(prev).set(call.id, Date.now()));
    setTimeout(() => setFresh(prev => {
      const next = new Map(prev);
      next.delete(call.id);
      return next;
    }), FRESH_MS);
    if (followRef.current) setSelectedId(call.id);
  }, []);
  const connected = useLiveLogs(onLiveLog);

  const visible = useMemo(() => {
    const shown = hiddenTypes.size === 0
      ? new Set<string>()
      : new Set(GROUP_ORDER.flatMap(typesInGroup).filter(t => !hiddenTypes.has(t)));
    // A hidden-set that hides everything shows nothing; express that as an impossible type.
    if (hiddenTypes.size > 0 && shown.size === 0) shown.add('\u0000none');
    return filterMapCalls(calls, { callTypes: shown, service, sinceMs: null });
  }, [calls, hiddenTypes, service]);

  const groupCounts = useMemo(() => {
    const counts: Record<CallGroup, number> = { fire: 0, medical: 0, police: 0, other: 0 };
    for (const c of calls) {
      if (service === 'all' || c.service === service) counts[groupOf(c.callType)]++;
    }
    return counts;
  }, [calls, service]);

  const groupState = (group: CallGroup): 'on' | 'off' | 'partial' => {
    const types = typesInGroup(group);
    const hidden = types.filter(t => hiddenTypes.has(t)).length;
    return hidden === 0 ? 'on' : hidden === types.length ? 'off' : 'partial';
  };

  const toggleGroup = (group: CallGroup) => {
    const types = typesInGroup(group);
    setHiddenTypes(prev => {
      const next = new Set(prev);
      if (groupState(group) === 'on') types.forEach(t => next.add(t));
      else types.forEach(t => next.delete(t));
      return next;
    });
  };

  const toggleType = (type: string) => setHiddenTypes(prev => {
    const next = new Set(prev);
    if (next.has(type)) next.delete(type); else next.add(type);
    return next;
  });

  const play = useCallback((call: MapCall) => {
    if (!call.audio_path) return;
    const label = `${callTypeLabel(call.callType)} · ${call.alphaTag || `${call.frequency.toFixed(4)} MHz`}`;
    void audio.playRawAudio(call.id, call.audio_path, call.duration ?? undefined, label);
  }, [audio]);

  const select = useCallback((call: MapCall) => {
    setSelectedId(call.id);
    if (!isDesktop) setListOpen(false);
  }, [isDesktop]);

  const liveAudioButton = (
    <Tooltip title={listenLive ? 'Stop live audio' : 'Listen to live scanner audio'}>
      <Button
        size="small"
        variant={listenLive ? 'contained' : 'outlined'}
        startIcon={<HeadphonesIcon />}
        onClick={() => { setListenLive(v => !v); void audio.initAudio(); }}
        aria-pressed={listenLive}
        sx={{ whiteSpace: 'nowrap' }}
      >
        Live audio
      </Button>
    </Tooltip>
  );

  const loading = loadedRange !== range;
  const aiReady = caps?.supported && caps.enabled;

  const list = (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      <Box sx={{ px: 2, py: 1.25, display: 'flex', alignItems: 'center', gap: 1, borderBottom: '1px solid', borderColor: 'surface.border' }}>
        <Typography variant="subtitle2" sx={{ fontWeight: 700, flexGrow: 1 }}>
          Calls <Box component="span" sx={{ color: 'text.secondary', fontWeight: 400 }}>· {visible.length}</Box>
        </Typography>
        {!isDesktop && (
          <IconButton size="small" onClick={() => setListOpen(false)} aria-label="Close list"><CloseIcon fontSize="small" /></IconButton>
        )}
      </Box>
      <Box sx={{ flex: 1, overflowY: 'auto', minHeight: 0 }}>
        {visible.length === 0 ? (
          <EmptyState dense title="No calls" hint={loading ? 'Loading…' : 'Nothing located matches these filters.'} />
        ) : visible.slice(0, LIST_LIMIT).map(call => {
          const selected = call.id === selectedId;
          const playing = audio.playingId === call.id;
          return (
            <Box
              key={call.id}
              role="button"
              tabIndex={0}
              aria-pressed={selected}
              onClick={() => select(call)}
              onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); select(call); } }}
              sx={{
                display: 'flex', gap: 1.25, px: 2, py: 1.25, cursor: 'pointer',
                borderBottom: '1px solid', borderColor: 'surface.border',
                bgcolor: selected ? 'surface.overlay' : 'transparent',
                '&:hover': { bgcolor: 'surface.raised' },
                animation: fresh.has(call.id) ? 'rowEnter 300ms ease' : undefined,
              }}
            >
              <Box sx={{ pt: 0.6 }}><GroupGlyph group={groupOf(call.callType)} /></Box>
              <Box sx={{ minWidth: 0, flex: 1 }}>
                <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 1 }}>
                  <Typography variant="body2" sx={{ fontWeight: 700, flex: 1 }} noWrap>
                    {callTypeLabel(call.callType)}
                    {call.incidentStatus && (
                      <Box component="span" sx={{ color: 'text.secondary', fontWeight: 400 }}> · {call.incidentStatus.replace(/_/g, ' ')}</Box>
                    )}
                  </Typography>
                  <Typography variant="caption" color="text.secondary" sx={{ flexShrink: 0 }}>{relTime(call.timestamp, now)}</Typography>
                </Box>
                {call.summary && <Typography variant="caption" sx={{ display: 'block', color: 'text.primary' }}>{call.summary}</Typography>}
                <Typography variant="caption" color="text.secondary" noWrap sx={{ display: 'block' }}>
                  {call.matchedAddress || call.address || call.placeName}
                </Typography>
                <Typography variant="caption" color="text.disabled" noWrap sx={{ display: 'block' }}>{call.alphaTag}</Typography>
              </Box>
              {call.audio_path && (
                <Tooltip title={playing ? 'Stop' : 'Play recording'}>
                  <IconButton
                    size="small"
                    onClick={(e) => { e.stopPropagation(); play(call); }}
                    aria-label={playing ? 'Stop recording' : 'Play recording'}
                    sx={{ alignSelf: 'center', color: playing ? 'primary.main' : 'text.secondary' }}
                  >
                    {playing ? <StopIcon fontSize="small" /> : <PlayArrowIcon fontSize="small" />}
                  </IconButton>
                </Tooltip>
              )}
            </Box>
          );
        })}
        {visible.length > LIST_LIMIT && (
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', p: 2, textAlign: 'center' }}>
            Showing the newest {LIST_LIMIT} of {visible.length}. All are on the map.
          </Typography>
        )}
      </Box>
    </Box>
  );

  return (
    <Box sx={{ height: '100dvh', display: 'flex', flexDirection: 'column', bgcolor: 'background.default', color: 'text.primary' }}>
      {/* Top bar: filters only, no radio controls. */}
      <Box
        component="header"
        sx={{
          display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 1.5,
          px: { xs: 1.5, sm: 2 }, py: 1, borderBottom: '1px solid', borderColor: 'surface.border', bgcolor: 'surface.surface',
        }}
      >
        <Stack direction="row" spacing={1} alignItems="center" sx={{ mr: 1, flexGrow: { xs: 1, md: 0 } }}>
          <MapIcon sx={{ color: 'primary.main' }} />
          <Typography variant="h6" sx={{ fontWeight: 700, fontSize: '1.05rem' }}>Call map</Typography>
          <Chip
            size="small"
            label={connected ? 'LIVE' : 'OFFLINE'}
            color={connected ? 'primary' : 'default'}
            variant={connected ? 'filled' : 'outlined'}
            sx={{ fontSize: 10, height: 20, fontWeight: 700 }}
          />
        </Stack>

        {/* Phones: Live audio and the list button sit on the title row; the filters
            below scroll sideways in one row instead of wrapping into several. */}
        <Box sx={{ display: { xs: 'flex', md: 'none' }, gap: 1, alignItems: 'center' }}>
          {liveAudioButton}
          <IconButton onClick={() => setListOpen(true)} aria-label="Show call list"><ListIcon /></IconButton>
        </Box>
        <Box sx={scrollRow}>
        <SegmentedControl<Range>
          size="small"
          aria-label="Time range"
          value={range}
          onChange={setRange}
          segments={(['1h', '6h', '24h', '7d', '30d'] as Range[]).map(r => ({ value: r, label: r }))}
        />
        <SegmentedControl<ServiceFilter>
          size="small"
          aria-label="Service"
          value={service}
          onChange={setService}
          segments={[{ value: 'all', label: 'All' }, { value: 'police', label: 'Police' }, { value: 'fire', label: 'Fire/EMS' }]}
        />
        <SegmentedControl<LayerMode>
          size="small"
          aria-label="Map layers"
          value={layers}
          onChange={setLayers}
          segments={[{ value: 'markers', label: 'Markers' }, { value: 'heat', label: 'Heat' }, { value: 'both', label: 'Both' }]}
        />

        <Box sx={{ flexGrow: 1, display: { xs: 'none', md: 'block' } }} />

        <Tooltip title="Select the newest call as it arrives">
          <Stack direction="row" alignItems="center" component="label" sx={{ cursor: 'pointer' }}>
            <Switch size="small" checked={follow} onChange={(e) => setFollow(e.target.checked)} />
            <Typography variant="caption" color="text.secondary" noWrap>Follow new calls</Typography>
          </Stack>
        </Tooltip>
        <Box sx={{ display: { xs: 'none', md: 'block' } }}>{liveAudioButton}</Box>
        </Box>
      </Box>

      {/* Call-type filter: one chip per color group, plus a menu for single types. */}
      <Box sx={{ ...scrollRow, flexGrow: 0, flexBasis: 'auto', flexShrink: 0, px: { xs: 1.5, sm: 2 }, py: 0.75, borderBottom: '1px solid', borderColor: 'surface.border', bgcolor: 'surface.surface' }}>
        {GROUP_ORDER.map(group => {
          const state = groupState(group);
          return (
            <Chip
              key={group}
              size="small"
              icon={<Box sx={{ display: 'flex', pl: 0.75 }}><GroupGlyph group={group} /></Box>}
              label={`${CALL_GROUPS[group].label} · ${groupCounts[group]}`}
              onClick={() => toggleGroup(group)}
              variant={state === 'off' ? 'outlined' : 'filled'}
              aria-pressed={state !== 'off'}
              sx={{
                opacity: state === 'off' ? 0.55 : 1,
                bgcolor: state === 'off' ? 'transparent' : 'surface.overlay',
                borderStyle: state === 'partial' ? 'dashed' : undefined,
              }}
            />
          );
        })}
        <Button size="small" color="inherit" startIcon={<TuneIcon />} onClick={(e) => setTypeMenu(e.currentTarget)} sx={{ color: 'text.secondary' }}>
          Call types{hiddenTypes.size > 0 ? ` (${hiddenTypes.size} hidden)` : ''}
        </Button>
        {hiddenTypes.size > 0 && (
          <Button size="small" color="inherit" onClick={() => setHiddenTypes(new Set())} sx={{ color: 'text.secondary' }}>Show all</Button>
        )}
        <Menu anchorEl={typeMenu} open={!!typeMenu} onClose={() => setTypeMenu(null)} slotProps={{ paper: { sx: { maxHeight: 420 } } }}>
          {GROUP_ORDER.map((group, gi) => [
            gi > 0 ? <Divider key={`${group}-div`} /> : null,
            <Typography key={`${group}-h`} variant="overline" sx={{ px: 2, display: 'flex', alignItems: 'center', gap: 1, color: 'text.secondary' }}>
              <GroupGlyph group={group} /> {CALL_GROUPS[group].label}
            </Typography>,
            ...typesInGroup(group).map(type => (
              <MenuItem key={type} dense onClick={() => toggleType(type)}>
                <ListItemIcon><Checkbox size="small" edge="start" checked={!hiddenTypes.has(type)} tabIndex={-1} disableRipple /></ListItemIcon>
                <ListItemText>{callTypeLabel(type)}</ListItemText>
              </MenuItem>
            )),
          ])}
        </Menu>
      </Box>

      {/* Map + list */}
      <Box sx={{ flex: 1, display: 'flex', minHeight: 0 }}>
        <Box sx={{ position: 'relative', flex: 1, minWidth: 0 }}>
          <CallMap
            calls={visible}
            layers={layers}
            selectedId={selectedId}
            fresh={fresh}
            fitKey={fitKey}
            onSelect={select}
            onPlay={play}
          />
          {(loading || (caps && !aiReady) || loadError || visible.length === 0) && (
            <Box
              sx={{
                position: 'absolute', zIndex: 500, left: '50%', top: 16, transform: 'translateX(-50%)',
                bgcolor: 'surface.raised', border: '1px solid', borderColor: 'surface.border', borderRadius: 2,
                boxShadow: 6, maxWidth: 'calc(100% - 32px)', pointerEvents: loading ? 'none' : 'auto',
              }}
            >
              {loading ? (
                <Stack direction="row" spacing={1} alignItems="center" sx={{ px: 2, py: 1 }}>
                  <CircularProgress size={14} /><Typography variant="caption">Loading calls…</Typography>
                </Stack>
              ) : caps && !aiReady ? (
                <EmptyState
                  dense
                  icon={<MapIcon />}
                  title={caps.supported ? 'AI call analysis is off' : 'AI call analysis is not available'}
                  hint={caps.supported
                    ? 'Turn on “AI call analysis” in Settings → Transcription to put calls on the map.'
                    : 'It needs a remote WhisperServer that supports /transmission. Set one up in Settings → Transcription.'}
                />
              ) : loadError ? (
                <EmptyState dense title="Could not load calls" hint="The server did not respond. Live calls will still appear." />
              ) : (
                <EmptyState dense title="No located calls in this range" hint="Try a longer time range or show more call types." />
              )}
            </Box>
          )}
        </Box>
        {isDesktop ? (
          <Box sx={{ width: 360, flexShrink: 0, borderLeft: '1px solid', borderColor: 'surface.border', bgcolor: 'surface.surface', minHeight: 0 }}>
            {list}
          </Box>
        ) : (
          <Drawer
            anchor="bottom"
            open={listOpen}
            onClose={() => setListOpen(false)}
            slotProps={{ paper: { sx: { height: '70dvh', bgcolor: 'surface.surface', borderTopLeftRadius: 12, borderTopRightRadius: 12 } } }}
          >
            {list}
          </Drawer>
        )}
      </Box>

      {audio.nowPlaying && (
        <NowPlayingBar
          nowPlaying={audio.nowPlaying}
          isPaused={audio.isPaused}
          getPosition={audio.getPosition}
          playbackRate={audio.playbackRate}
          onTogglePause={audio.togglePause}
          onSeek={audio.seek}
          onSetRate={audio.setRate}
          onStop={audio.stop}
        />
      )}
    </Box>
  );
};

export default MapPage;
