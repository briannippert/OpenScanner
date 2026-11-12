import { Suspense, useEffect, useState, useCallback, useMemo, useRef } from 'react';
import { Box, Paper, Grid, Snackbar, Alert } from '@mui/material';
import AppHeader from './components/AppHeader';
import ScannerDisplay from './components/ScannerDisplay';
import ChannelGrid from './components/ChannelGrid';
import EventLog from './components/EventLog';
import TransmissionLog from './components/TransmissionLog';
import NowPlayingBar from './components/NowPlayingBar';
import ConnectionOverlay from './components/ConnectionOverlay';
import {
  ChannelManager, FireToneManager, SettingsManager, AliasManager, RfDebugDialog,
  SystemDebugDialog, StatsDialog, UpdateManager, CommandPalette, preloadDialogs,
} from './lazyDialogs';
import { useAudioPipeline } from './hooks/useAudioPipeline';
import { useScannerSocket } from './hooks/useScannerSocket';
import { useDialogs } from './hooks/useDialogs';
import { apiJson } from './components/common/apiBase';
import { sameFrequency } from './lib/frequency';
import type { AiCapabilities, Channel, RadioEvent, UpdateStatus } from './types';

function App() {
  const [volume, setVolume] = useState<number>(() => {
    const saved = localStorage.getItem('scannerVolume');
    return saved !== null ? parseFloat(saved) : 1.0;
  });
  const [isFullscreen, setIsFullscreen] = useState(false);
  const [highlightLog, setHighlightLog] = useState<{ id: string; seq: number } | null>(null);
  const [polledAvailable, setPolledAvailable] = useState(false);
  const [debugFreq, setDebugFreq] = useState<string>('155.500');
  const [debugGain, setDebugGain] = useState<number>(40);
  // Which SDR the debug sweep uses; empty for the first. The others keep scanning.
  const [debugDevice, setDebugDevice] = useState<string>('');

  const dialogs = useDialogs();
  const dialog = dialogs.handlers;

  const audio = useAudioPipeline(volume);
  const scanner = useScannerSocket();
  // `scanner` is a new object every render, but these members are stable. Callbacks
  // below depend on them individually so that they stay stable too: this component
  // re-renders on every scanner state update, and an unstable callback would drag every
  // memoized child along with it.
  const {
    scannerState, sendCommand, handleSaveChannel, handleSkip, setCallLog, setErrorMsg,
    refreshAliasCandidates,
  } = scanner;
  const manualHold = scannerState.manualHoldFrequency;

  // Fetch the dialog code once the dashboard itself is up and idle.
  useEffect(() => {
    if (typeof window.requestIdleCallback === 'function') {
      const id = window.requestIdleCallback(preloadDialogs, { timeout: 5000 });
      return () => window.cancelIdleCallback(id);
    }
    const id = setTimeout(preloadDialogs, 2000);
    return () => clearTimeout(id);
  }, []);

  // The call map is offered only when AI call analysis is supported and on. Re-checked
  // whenever Settings closes, since that is where it gets switched on.
  const settingsOpen = dialogs.isOpen('settings');
  const [mapAvailable, setMapAvailable] = useState(false);
  useEffect(() => {
    if (settingsOpen) return;
    let cancelled = false;
    void apiJson<AiCapabilities>('/api/ai/capabilities').then(caps => {
      if (!cancelled) setMapAvailable(!!caps?.supported && caps.enabled);
    });
    return () => { cancelled = true; };
  }, [settingsOpen]);
  const openMap = useCallback(() => { window.open('/map', '_blank', 'noopener'); }, []);

  // Poll update availability for the ribbon indicator.
  useEffect(() => {
    let cancelled = false;
    const check = async () => {
      const s = await apiJson<UpdateStatus>('/api/update/status');
      if (!cancelled && s) setPolledAvailable(s.updateAvailable);
    };
    check();
    const id = setInterval(check, 120000);
    return () => { cancelled = true; clearInterval(id); };
  }, []);

  // Ribbon indicator: show when the poll or a live WS check reports an update,
  // hidden only while an update is actively running or just finished.
  const updateState = scanner.updateState;
  const updateAvailable = (updateState === 'available' || polledAvailable)
    && updateState !== 'updating' && updateState !== 'success';

  // Command palette: Ctrl/⌘-K toggles it from anywhere.
  const togglePalette = dialog.palette.toggle;
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        togglePalette();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [togglePalette]);

  // Fullscreen state tracking.
  useEffect(() => {
    const onChange = () => setIsFullscreen(!!document.fullscreenElement);
    document.addEventListener('fullscreenchange', onChange);
    return () => document.removeEventListener('fullscreenchange', onChange);
  }, []);

  const toggleFullscreen = useCallback(() => {
    if (!document.fullscreenElement) {
      document.documentElement.requestFullscreen().catch(err =>
        console.error(`Error enabling full-screen: ${err.message} (${err.name})`));
    } else {
      document.exitFullscreen().catch(err =>
        console.error(`Error exiting full-screen: ${err.message} (${err.name})`));
    }
  }, []);

  // Keep the screen awake while the scanner is active.
  useEffect(() => {
    let wakeLock: WakeLockSentinel | null = null;
    const request = async () => {
      if ('wakeLock' in navigator && scannerState.status !== 'IDLE' && navigator.wakeLock) {
        try {
          if (!wakeLock) {
            wakeLock = await navigator.wakeLock.request('screen');
            wakeLock.addEventListener('release', () => { wakeLock = null; });
          }
        } catch (err) {
          if (err instanceof Error) console.error(`${err.name}, ${err.message}`);
        }
      } else if (wakeLock && scannerState.status === 'IDLE') {
        wakeLock.release();
        wakeLock = null;
      }
    };
    request();
    const onVisible = () => {
      if (document.visibilityState === 'visible' && scannerState.status !== 'IDLE') request();
    };
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      document.removeEventListener('visibilitychange', onVisible);
      wakeLock?.release().catch(err => console.warn('Failed to release wake lock:', err));
    };
  }, [scannerState.status]);

  const downloadSupportPackage = useCallback(() => {
    const link = document.createElement('a');
    link.href = '/api/support/package';
    link.setAttribute('download', '');
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
  }, []);

  const resumeScan = useCallback(() => { sendCommand('scan'); }, [sendCommand]);

  const tuneDebug = useCallback(() => {
    sendCommand('debug_spectrum', Number(debugFreq), debugGain, debugDevice || undefined);
  }, [sendCommand, debugFreq, debugGain, debugDevice]);

  const openRfDebug = dialog.rfDebug.open;
  const closeRfDebug = dialog.rfDebug.close;
  const openDebug = useCallback(() => {
    openRfDebug();
    tuneDebug();
  }, [openRfDebug, tuneDebug]);

  const isDebugging = scannerState.status === 'DEBUG';
  const closeDebug = useCallback(() => {
    if (isDebugging) sendCommand('scan');
    closeRfDebug();
  }, [sendCommand, isDebugging, closeRfDebug]);

  // Hold/resume a channel from the grid.
  const handleHold = useCallback(async (ch: Channel) => {
    if (window.audioCtx && window.audioCtx.state === 'suspended') window.audioCtx.resume();
    if (sameFrequency(manualHold, ch.frequency)) {
      sendCommand('scan');
    } else {
      if (ch.avoid) await handleSaveChannel({ ...ch, avoid: false });
      sendCommand('hold', ch.frequency);
    }
  }, [manualHold, sendCommand, handleSaveChannel]);

  const handleToggleAvoid = useCallback(async (ch: Channel) => {
    const newAvoid = !ch.avoid;
    await handleSaveChannel({ ...ch, avoid: newAvoid });
    // Stop holding a channel we've just started avoiding.
    if (newAvoid && sameFrequency(manualHold, ch.frequency)) {
      sendCommand('scan');
    }
  }, [manualHold, sendCommand, handleSaveChannel]);

  // Skip reads the tuned frequency through a ref: it changes constantly while scanning,
  // and the callback goes to the (memoized) command palette.
  const currentFrequencyRef = useRef(scannerState.currentFrequency);
  useEffect(() => { currentFrequencyRef.current = scannerState.currentFrequency; }, [scannerState.currentFrequency]);
  const skipCurrent = useCallback(() => handleSkip(currentFrequencyRef.current), [handleSkip]);

  const handleEventClick = useCallback((e: RadioEvent) => {
    const id = e.transmissionId;
    if (id) setHighlightLog(h => ({ id, seq: (h?.seq ?? 0) + 1 }));
  }, []);

  const clearCallLog = useCallback(() => setCallLog([]), [setCallLog]);
  const loadAliasCandidates = useCallback(() => refreshAliasCandidates(7), [refreshAliasCandidates]);
  const dismissError = useCallback(() => setErrorMsg(null), [setErrorMsg]);

  const showAudioPrompt = !audio.isAudioInitialized &&
    (scannerState.status === 'RECEIVING' || scannerState.status === 'MONITORING');

  // Recording ids referenced by fire tone-out events (drives the Tone-outs filter).
  const toneOutIds = useMemo(
    () => new Set(scanner.radioEvents.filter(e => e.transmissionId).map(e => e.transmissionId!)),
    [scanner.radioEvents],
  );

  // Recording id → transcription for the fire tone-out event log. Value is null
  // while the recording is present but its transcription hasn't streamed in yet,
  // so EventLog can show a "Transcribing…" placeholder until it arrives.
  const eventTranscriptions = useMemo(() => {
    const map = new Map<string, string | null>();
    for (const log of scanner.callLog) map.set(log.id, log.transcription ?? null);
    return map;
  }, [scanner.callLog]);

  return (
      <Box sx={{
        display: 'flex', flexDirection: 'column', height: '100vh', width: '100vw',
        bgcolor: 'background.default', overflow: 'hidden', position: 'relative',
      }}>
        <ConnectionOverlay isConnected={scanner.isConnected} hasConnected={scanner.hasConnected} reconnectAt={scanner.reconnectAt} />

        <AppHeader
          isHardwareConnected={scannerState.isHardwareConnected}
          deviceName={scannerState.deviceName}
          devicePort={scannerState.devicePort}
          gps={scannerState.gps}
          volume={volume}
          onVolumeChange={setVolume}
          manualHold={manualHold}
          onResume={resumeScan}
          isFullscreen={isFullscreen}
          onToggleFullscreen={toggleFullscreen}
          onOpenFireTones={dialog.fireTones.open}
          onOpenSettings={dialog.settings.open}
          onOpenStats={dialog.stats.open}
          onOpenAliases={dialog.aliases.open}
          updateAvailable={updateAvailable}
          onOpenUpdate={dialog.update.open}
          onOpenPalette={dialog.palette.open}
          onOpenMap={mapAvailable ? openMap : undefined}
        />

        <Box sx={{ flexGrow: 1, p: { xs: 1, sm: 2 }, height: '100%', overflowY: { xs: 'auto', md: 'hidden' } }}>
          <Grid container spacing={2} sx={{ height: { xs: 'auto', md: '100%' } }}>
            {/* Left: scanner hero + channel grid */}
            <Grid size={{ xs: 12, md: 4, lg: 3 }} sx={{ display: 'flex', flexDirection: 'column', height: { xs: 'auto', md: '100%' }, overflow: { xs: 'visible', md: 'hidden' } }}>
              <Box sx={{ mb: 2 }}>
                <ScannerDisplay
                  state={scannerState}
                  analyser={audio.audioAnalyser}
                  channels={scanner.channels}
                  onScan={scanner.handleSkip}
                  nameFor={scanner.nameFor}
                />
              </Box>
              <ChannelGrid
                channels={scanner.channels}
                manualHold={manualHold}
                loaded={scanner.channelsLoaded}
                onEdit={dialog.channels.open}
                onToggleAvoid={handleToggleAvoid}
                onHold={handleHold}
              />
            </Grid>

            {/* Right: events + transmission log */}
            <Grid size={{ xs: 12, md: 8, lg: 9 }} sx={{ height: { xs: 500, md: '100%' }, overflow: 'hidden' }}>
              <Paper sx={{ height: '100%', display: 'flex', flexDirection: 'column', borderRadius: 2, overflow: 'hidden' }}>
                {scanner.fireTones.length > 0 && (
                  <EventLog
                    events={scanner.radioEvents}
                    transcriptions={eventTranscriptions}
                    onClear={scanner.clearEvents}
                    onEventClick={handleEventClick}
                  />
                )}
                <TransmissionLog
                  liveLogs={scanner.callLog}
                  playingId={audio.playingId}
                  onPlay={audio.playRawAudio}
                  onDelete={scanner.deleteEntry}
                  highlight={highlightLog}
                  loaded={scanner.logLoaded}
                  toneOutIds={toneOutIds}
                  nameFor={scanner.nameFor}
                />
              </Paper>
            </Grid>
          </Grid>
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

        {/* Dialogs are code-split and mounted on first open; after that they stay mounted
            so their close animations and internal state behave as before. */}
        <Suspense fallback={null}>
          {dialogs.wasOpened('channels') && (
            <ChannelManager
              open={dialogs.isOpen('channels')}
              onClose={dialog.channels.close}
              channels={scanner.channels}
              onSave={scanner.handleSaveChannel}
              onDelete={scanner.handleDeleteChannel}
            />
          )}
          {dialogs.wasOpened('fireTones') && (
            <FireToneManager
              open={dialogs.isOpen('fireTones')}
              onClose={dialog.fireTones.close}
              tones={scanner.fireTones}
              onSave={scanner.handleSaveFireTone}
              onDelete={scanner.handleDeleteFireTone}
            />
          )}
          {dialogs.wasOpened('settings') && (
            <SettingsManager
              open={dialogs.isOpen('settings')}
              onClose={dialog.settings.close}
              onRecordingsDeleted={clearCallLog}
              // Only needed while open; withholding it lets the closed dialog skip state updates.
              scannerState={dialogs.isOpen('settings') ? scannerState : undefined}
              onOpenSystemDebug={dialog.systemDebug.open}
              onOpenRfDebug={openDebug}
            />
          )}
          {dialogs.wasOpened('aliases') && (
            <AliasManager
              open={dialogs.isOpen('aliases')}
              onClose={dialog.aliases.close}
              candidates={scanner.aliasCandidates}
              aliases={scanner.aliases}
              onSave={scanner.handleSaveAlias}
              onDelete={scanner.handleDeleteAlias}
              onImport={scanner.importAliases}
              onOpened={loadAliasCandidates}
            />
          )}
          {dialogs.wasOpened('update') && (
            <UpdateManager
              open={dialogs.isOpen('update')}
              onClose={dialog.update.close}
              log={scanner.updateLog}
              state={scanner.updateState}
              onSeed={scanner.seedUpdate}
            />
          )}
          {dialogs.wasOpened('palette') && (
            <CommandPalette
              open={dialogs.isOpen('palette')}
              onClose={dialog.palette.close}
              channels={scanner.channels}
              manualHold={manualHold}
              onHold={handleHold}
              onResume={resumeScan}
              onSkip={skipCurrent}
              onOpenSettings={dialog.settings.open}
              onOpenFireTones={dialog.fireTones.open}
              onOpenChannels={dialog.channels.open}
              onOpenAliases={dialog.aliases.open}
              onOpenStats={dialog.stats.open}
              onOpenDebug={openDebug}
              onOpenSystemDebug={dialog.systemDebug.open}
              onToggleFullscreen={toggleFullscreen}
              onOpenMap={mapAvailable ? openMap : undefined}
            />
          )}
          {dialogs.wasOpened('rfDebug') && (
            <RfDebugDialog
              open={dialogs.isOpen('rfDebug')}
              onClose={closeDebug}
              scannerState={scannerState}
              debugFreq={debugFreq}
              onDebugFreqChange={setDebugFreq}
              debugGain={debugGain}
              onDebugGainChange={setDebugGain}
              debugDevice={debugDevice}
              onDebugDeviceChange={setDebugDevice}
              onTune={tuneDebug}
            />
          )}
          {dialogs.wasOpened('stats') && (
            <StatsDialog
              open={dialogs.isOpen('stats')}
              onClose={dialog.stats.close}
              nameFor={scanner.nameFor}
            />
          )}
          {dialogs.wasOpened('systemDebug') && (
            <SystemDebugDialog
              open={dialogs.isOpen('systemDebug')}
              onClose={dialog.systemDebug.close}
              onDownloadSupport={downloadSupportPackage}
            />
          )}
        </Suspense>

        <Snackbar open={showAudioPrompt} anchorOrigin={{ vertical: 'top', horizontal: 'center' }}>
          <Alert severity="info" variant="filled" sx={{ width: '100%', cursor: 'pointer' }} onClick={audio.initAudio}>
            Click anywhere to enable live audio
          </Alert>
        </Snackbar>
        <Snackbar
          open={!!scanner.errorMsg}
          autoHideDuration={6000}
          onClose={dismissError}
          anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
        >
          <Alert onClose={dismissError} severity="error" variant="filled" sx={{ width: '100%' }}>
            {scanner.errorMsg}
          </Alert>
        </Snackbar>
      </Box>
  );
}

export default App;
