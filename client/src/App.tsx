import { useEffect, useState, useCallback, useMemo } from 'react';
import { Box, Paper, Grid, Typography, Snackbar, Alert, CircularProgress } from '@mui/material';
import AppHeader from './components/AppHeader';
import ScannerDisplay from './components/ScannerDisplay';
import ChannelGrid from './components/ChannelGrid';
import EventLog from './components/EventLog';
import TransmissionLog from './components/TransmissionLog';
import ChannelManager from './components/ChannelManager';
import FireToneManager from './components/FireToneManager';
import SettingsManager from './components/SettingsManager';
import AliasManager from './components/AliasManager';
import RfDebugDialog from './components/RfDebugDialog';
import SystemDebugDialog from './components/SystemDebugDialog';
import UpdateManager from './components/UpdateManager';
import NowPlayingBar from './components/NowPlayingBar';
import CommandPalette from './components/CommandPalette';
import { useAudioPipeline } from './hooks/useAudioPipeline';
import { useScannerSocket } from './hooks/useScannerSocket';
import { apiJson } from './components/common/apiBase';
import type { Channel, UpdateStatus } from './types';

function App() {
  const [currentTime, setCurrentTime] = useState(new Date());
  const [volume, setVolume] = useState<number>(() => {
    const saved = localStorage.getItem('scannerVolume');
    return saved !== null ? parseFloat(saved) : 1.0;
  });
  const [isFullscreen, setIsFullscreen] = useState(false);
  const [highlightLog, setHighlightLog] = useState<{ id: string; seq: number } | null>(null);

  const [isManagerOpen, setIsManagerOpen] = useState(false);
  const [isToneManagerOpen, setIsToneManagerOpen] = useState(false);
  const [isSettingsOpen, setIsSettingsOpen] = useState(false);
  const [isDebugModalOpen, setIsDebugModalOpen] = useState(false);
  const [isSystemDebugOpen, setIsSystemDebugOpen] = useState(false);
  const [isAliasManagerOpen, setIsAliasManagerOpen] = useState(false);
  const [isUpdateOpen, setIsUpdateOpen] = useState(false);
  const [polledAvailable, setPolledAvailable] = useState(false);
  const [isPaletteOpen, setIsPaletteOpen] = useState(false);
  const [debugFreq, setDebugFreq] = useState<string>('155.500');
  const [debugGain, setDebugGain] = useState<number>(40);

  const audio = useAudioPipeline(volume);
  const scanner = useScannerSocket({ onParallel: audio.setParallel });
  const { scannerState } = scanner;
  const manualHold = scannerState.manualHoldFrequency;

  // Clock.
  useEffect(() => {
    const timer = setInterval(() => setCurrentTime(new Date()), 1000);
    return () => clearInterval(timer);
  }, []);

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
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        setIsPaletteOpen(o => !o);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

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

  const openDebug = useCallback(() => {
    setIsDebugModalOpen(true);
    scanner.sendCommand('debug_spectrum', Number(debugFreq), debugGain);
  }, [scanner, debugFreq, debugGain]);

  const closeDebug = useCallback(() => {
    if (scannerState.status === 'DEBUG') scanner.sendCommand('scan');
    setIsDebugModalOpen(false);
  }, [scanner, scannerState.status]);

  // Hold/resume a channel from the grid.
  const handleHold = useCallback(async (ch: Channel) => {
    if (window.audioCtx && window.audioCtx.state === 'suspended') window.audioCtx.resume();
    const isHolding = manualHold !== undefined && Math.abs(manualHold - ch.frequency) < 0.0001;
    if (isHolding) {
      scanner.sendCommand('scan');
    } else {
      if (ch.avoid) await scanner.handleSaveChannel({ ...ch, avoid: false });
      scanner.sendCommand('hold', ch.frequency);
    }
  }, [manualHold, scanner]);

  const handleToggleAvoid = useCallback(async (ch: Channel) => {
    const newAvoid = !ch.avoid;
    await scanner.handleSaveChannel({ ...ch, avoid: newAvoid });
    // Stop holding a channel we've just started avoiding.
    if (newAvoid && manualHold !== undefined && Math.abs(manualHold - ch.frequency) < 0.0001) {
      scanner.sendCommand('scan');
    }
  }, [manualHold, scanner]);

  const showAudioPrompt = !audio.isAudioInitialized &&
    (scannerState.status === 'RECEIVING' || scannerState.status === 'MONITORING');

  const reconnectSeconds = scanner.reconnectAt != null
    ? Math.max(0, Math.ceil((scanner.reconnectAt - currentTime.getTime()) / 1000))
    : null;

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
        {!scanner.isConnected && (
          <Box sx={{
            position: 'absolute', inset: 0, zIndex: 9999,
            bgcolor: 'rgba(0,0,0,0.8)', backdropFilter: 'blur(4px)',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
          }}>
            <Paper sx={{ p: 4, textAlign: 'center', borderColor: 'error.main', minWidth: 300 }}>
              <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 1.5, mb: 1 }}>
                <CircularProgress size={20} color="error" />
                <Typography variant="h5" color="error" fontWeight={700}>CONNECTION LOST</Typography>
              </Box>
              <Typography color="text.secondary">
                {reconnectSeconds != null && reconnectSeconds > 0
                  ? `Reconnecting to OpenScanner server in ${reconnectSeconds}s…`
                  : 'Reconnecting to OpenScanner server…'}
              </Typography>
            </Paper>
          </Box>
        )}

        <AppHeader
          scannerState={scannerState}
          currentTime={currentTime}
          volume={volume}
          onVolumeChange={setVolume}
          manualHold={manualHold}
          onResume={() => scanner.sendCommand('scan')}
          isFullscreen={isFullscreen}
          onToggleFullscreen={toggleFullscreen}
          onOpenFireTones={() => setIsToneManagerOpen(true)}
          onOpenSettings={() => setIsSettingsOpen(true)}
          onOpenDebug={openDebug}
          onOpenSystemDebug={() => setIsSystemDebugOpen(true)}
          onOpenAliases={() => setIsAliasManagerOpen(true)}
          updateAvailable={updateAvailable}
          onOpenUpdate={() => setIsUpdateOpen(true)}
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
                onEdit={() => setIsManagerOpen(true)}
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
                    onEventClick={(e) => {
                      if (e.transmissionId) {
                        setHighlightLog(h => ({ id: e.transmissionId!, seq: (h?.seq ?? 0) + 1 }));
                      }
                    }}
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
            positionSec={audio.positionSec}
            playbackRate={audio.playbackRate}
            onTogglePause={audio.togglePause}
            onSeek={audio.seek}
            onSetRate={audio.setRate}
            onStop={audio.stop}
          />
        )}

        <ChannelManager
          open={isManagerOpen}
          onClose={() => setIsManagerOpen(false)}
          channels={scanner.channels}
          onSave={scanner.handleSaveChannel}
          onDelete={scanner.handleDeleteChannel}
        />
        <FireToneManager
          open={isToneManagerOpen}
          onClose={() => setIsToneManagerOpen(false)}
          tones={scanner.fireTones}
          onSave={scanner.handleSaveFireTone}
          onDelete={scanner.handleDeleteFireTone}
        />
        <SettingsManager
          open={isSettingsOpen}
          onClose={() => setIsSettingsOpen(false)}
          onRecordingsDeleted={() => scanner.setCallLog([])}
        />
        <AliasManager
          open={isAliasManagerOpen}
          onClose={() => setIsAliasManagerOpen(false)}
          candidates={scanner.aliasCandidates}
          aliases={scanner.aliases}
          onSave={scanner.handleSaveAlias}
          onDelete={scanner.handleDeleteAlias}
          onImport={scanner.importAliases}
          onOpened={() => scanner.refreshAliasCandidates(7)}
        />
        <UpdateManager
          open={isUpdateOpen}
          onClose={() => setIsUpdateOpen(false)}
          log={scanner.updateLog}
          state={scanner.updateState}
          onSeed={scanner.seedUpdate}
        />
        <CommandPalette
          open={isPaletteOpen}
          onClose={() => setIsPaletteOpen(false)}
          channels={scanner.channels}
          manualHold={manualHold}
          onHold={handleHold}
          onResume={() => scanner.sendCommand('scan')}
          onSkip={() => scanner.handleSkip(scannerState.currentFrequency)}
          onOpenSettings={() => setIsSettingsOpen(true)}
          onOpenFireTones={() => setIsToneManagerOpen(true)}
          onOpenChannels={() => setIsManagerOpen(true)}
          onOpenAliases={() => setIsAliasManagerOpen(true)}
          onOpenDebug={openDebug}
          onToggleFullscreen={toggleFullscreen}
        />

        <RfDebugDialog
          open={isDebugModalOpen}
          onClose={closeDebug}
          scannerState={scannerState}
          debugFreq={debugFreq}
          onDebugFreqChange={setDebugFreq}
          debugGain={debugGain}
          onDebugGainChange={setDebugGain}
          onTune={() => scanner.sendCommand('debug_spectrum', Number(debugFreq), debugGain)}
        />

        <SystemDebugDialog
          open={isSystemDebugOpen}
          onClose={() => setIsSystemDebugOpen(false)}
          onDownloadSupport={downloadSupportPackage}
        />

        <Snackbar open={showAudioPrompt} anchorOrigin={{ vertical: 'top', horizontal: 'center' }}>
          <Alert severity="info" variant="filled" sx={{ width: '100%', cursor: 'pointer' }} onClick={audio.initAudio}>
            Click anywhere to enable live audio
          </Alert>
        </Snackbar>
        <Snackbar
          open={!!scanner.errorMsg}
          autoHideDuration={6000}
          onClose={() => scanner.setErrorMsg(null)}
          anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
        >
          <Alert onClose={() => scanner.setErrorMsg(null)} severity="error" variant="filled" sx={{ width: '100%' }}>
            {scanner.errorMsg}
          </Alert>
        </Snackbar>
      </Box>
  );
}

export default App;
