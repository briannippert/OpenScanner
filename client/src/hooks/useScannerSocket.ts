import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ScannerState, Channel, CallLog, FireToneSet, RadioEvent, RadioAlias, AliasCandidate, UpdateProgress, UpdateState } from '../types';
import type { NameFor } from '../lib/aliasLabels';

// Channel-scoped alias key; toFixed(4) neutralizes REAL float equality on both sides.
const aliasKey = (kind: string, value: number, alphaTag: string, frequency: number) =>
  `${alphaTag}|${frequency.toFixed(4)}|${kind}|${value}`;

/**
 * Owns the control WebSocket, initial REST loads, live scanner/channel/log/event
 * state, and the command + CRUD helpers. Extracted from App so the component is a
 * thin composition root over this hook and useAudioPipeline.
 */
export function useScannerSocket() {
  const [scannerState, setScannerState] = useState<ScannerState>({ status: 'IDLE', signalStrength: 0 });
  const [channels, setChannels] = useState<Channel[]>([]);
  const [fireTones, setFireTones] = useState<FireToneSet[]>([]);
  const [callLog, setCallLog] = useState<CallLog[]>([]);
  const [radioEvents, setRadioEvents] = useState<RadioEvent[]>([]);
  const [aliases, setAliases] = useState<RadioAlias[]>([]);
  const [aliasCandidates, setAliasCandidates] = useState<AliasCandidate[]>([]);
  const [updateLog, setUpdateLog] = useState<string[]>([]);
  const [updateState, setUpdateState] = useState<UpdateState>('idle');

  // Seed/replace the update console (from a status snapshot on open, or clear on start).
  const seedUpdate = useCallback((lines: string[], state: UpdateState) => {
    setUpdateLog(lines);
    setUpdateState(state);
  }, []);
  const [isConnected, setIsConnected] = useState(false);
  // False until the socket has opened once: "not connected yet" is not "connection lost".
  const [hasConnected, setHasConnected] = useState(false);
  const [reconnectAt, setReconnectAt] = useState<number | null>(null);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);
  // Distinguish "still loading" from a genuinely empty dataset for skeletons.
  const [channelsLoaded, setChannelsLoaded] = useState(false);
  const [logLoaded, setLogLoaded] = useState(false);

  const wsControl = useRef<WebSocket | null>(null);
  // Raw text of the last STATE_UPDATE applied, to drop exact repeats without parsing them.
  const lastStateRaw = useRef<string | null>(null);

  // Low-level REST helper for scanner control endpoints.
  const scannerApi = useCallback((path: string, method: string, body?: object) =>
    fetch(`/api/scanner/${path}`, {
      method,
      headers: body ? { 'Content-Type': 'application/json' } : undefined,
      body: body ? JSON.stringify(body) : undefined,
    }).catch(err => console.error('Command failed:', err)), []);

  // Map legacy action names onto REST endpoints.
  const sendCommand = useCallback((action: string, frequency?: number, value?: number) => {
    switch (action) {
      case 'scan': return scannerApi('hold', 'DELETE');
      case 'hold': return scannerApi('hold', 'PUT', { frequency });
      case 'start': return scannerApi('power', 'PUT', { enabled: true });
      case 'stop': return scannerApi('power', 'PUT', { enabled: false });
      case 'set_squelch': return scannerApi('squelch', 'PUT', { value });
      case 'set_gain': return scannerApi('gain', 'PUT', { value });
      case 'set_ppm': return scannerApi('ppm', 'PUT', { value });
      case 'debug_spectrum': return scannerApi('debug-spectrum', 'POST', { frequency, gain: value });
      default: console.error('Unknown command:', action);
    }
  }, [scannerApi]);

  const handleSkip = useCallback((freq?: number) => {
    if (freq) scannerApi('avoids', 'POST', { frequency: freq, duration: 10 });
    else sendCommand('scan');
  }, [scannerApi, sendCommand]);

  /**
   * Run a write request and report failure to the user. Resolves to the response on
   * success and null on failure, having already set the error message that App shows in
   * its snackbar: a save that the server rejects must not look like one that worked.
   */
  const request = useCallback(async (what: string, url: string, init?: RequestInit): Promise<Response | null> => {
    try {
      const res = await fetch(url, init);
      if (res.ok) return res;
      console.error(`${what} failed with status`, res.status);
      setErrorMsg(`${what} failed (the server returned ${res.status}).`);
    } catch (e) {
      console.error(`${what} failed:`, e);
      setErrorMsg(`${what} failed: could not reach the server.`);
    }
    return null;
  }, []);

  const jsonBody = (body: unknown): RequestInit => ({
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });

  const refreshChannels = useCallback(() => {
    fetch('/api/channels')
      .then(res => res.json())
      .then(data => setChannels(data))
      .catch(err => console.error('Failed to fetch channels:', err));
  }, []);

  const refreshFireTones = useCallback(() => {
    fetch('/api/firetones')
      .then(res => res.json())
      .then(data => setFireTones(data))
      .catch(err => console.error('Failed to fetch fire tones:', err));
  }, []);

  const handleSaveChannel = useCallback(async (channel: Channel) => {
    const method = channel.id ? 'PUT' : 'POST';
    const url = channel.id ? `/api/channels/${channel.id}` : '/api/channels';
    if (await request('Saving the channel', url, { method, ...jsonBody(channel) })) refreshChannels();
  }, [request, refreshChannels]);

  const handleDeleteChannel = useCallback(async (id: number) => {
    if (await request('Deleting the channel', `/api/channels/${id}`, { method: 'DELETE' })) refreshChannels();
  }, [request, refreshChannels]);

  const handleSaveFireTone = useCallback(async (tone: FireToneSet) => {
    const method = tone.id ? 'PUT' : 'POST';
    const url = tone.id ? `/api/firetones/${tone.id}` : '/api/firetones';
    if (await request('Saving the fire tone-out', url, { method, ...jsonBody(tone) })) refreshFireTones();
  }, [request, refreshFireTones]);

  const handleDeleteFireTone = useCallback(async (id: number) => {
    if (await request('Deleting the fire tone-out', `/api/firetones/${id}`, { method: 'DELETE' })) refreshFireTones();
  }, [request, refreshFireTones]);

  const refreshAliases = useCallback(() => {
    fetch('/api/aliases')
      .then(res => res.json())
      .then(data => setAliases(data))
      .catch(err => console.error('Failed to fetch aliases:', err));
  }, []);

  const refreshAliasCandidates = useCallback((days = 7) => {
    fetch(`/api/aliases/candidates?days=${days}`)
      .then(res => res.json())
      .then(data => setAliasCandidates(data))
      .catch(err => console.error('Failed to fetch alias candidates:', err));
  }, []);

  const handleSaveAlias = useCallback(async (alias: RadioAlias) => {
    const method = alias.id ? 'PUT' : 'POST';
    const url = alias.id ? `/api/aliases/${alias.id}` : '/api/aliases';
    if (await request('Saving the alias', url, { method, ...jsonBody(alias) })) refreshAliases();
  }, [request, refreshAliases]);

  const handleDeleteAlias = useCallback(async (id: number) => {
    if (await request('Deleting the alias', `/api/aliases/${id}`, { method: 'DELETE' })) refreshAliases();
  }, [request, refreshAliases]);

  // Additive import: fills in blanks server-side without overwriting existing names.
  // Returns the number of aliases added.
  const importAliases = useCallback(async (list: RadioAlias[]): Promise<number> => {
    const res = await request('Importing aliases', '/api/aliases/import', { method: 'POST', ...jsonBody(list) });
    if (!res) return 0;
    const data = await res.json().catch(() => ({ added: 0 }));
    refreshAliases();
    return data?.added ?? 0;
  }, [request, refreshAliases]);

  // App-wide per-channel name lookup for SRC/TG, rebuilt when aliases change.
  const aliasIndex = useMemo(() => {
    const m = new Map<string, string>();
    for (const a of aliases) m.set(aliasKey(a.kind, a.value, a.alphaTag, a.frequency), a.name);
    return m;
  }, [aliases]);

  const nameFor = useCallback<NameFor>(
    (kind, value, alphaTag, frequency) =>
      value == null ? undefined : aliasIndex.get(aliasKey(kind, value, alphaTag, frequency)),
    [aliasIndex],
  );

  const deleteEntry = useCallback(async (id: string) => {
    if (await request('Deleting the recording', `/api/history/${id}`, { method: 'DELETE' })) {
      setCallLog(prev => prev.filter(log => log.id !== id));
    }
  }, [request]);

  const clearEvents = useCallback(async () => {
    if (await request('Clearing tone-out events', '/api/events', { method: 'DELETE' })) setRadioEvents([]);
  }, [request]);

  // Initial data load + control WebSocket with auto-reconnect.
  useEffect(() => {
    const wsProtocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsControlUrl = `${wsProtocol}//${window.location.host}/ws/control`;
    let closed = false;

    fetch('/api/channels').then(r => r.json()).then(setChannels)
      .catch(err => console.error('Failed to fetch channels:', err)).finally(() => setChannelsLoaded(true));
    fetch('/api/firetones').then(r => r.json()).then(setFireTones)
      .catch(err => console.error('Failed to fetch fire tones:', err));
    fetch('/api/history').then(r => r.json()).then(setCallLog)
      .catch(err => console.error('Failed to fetch history:', err)).finally(() => setLogLoaded(true));
    fetch('/api/events').then(r => r.json()).then(setRadioEvents)
      .catch(err => console.error('Failed to fetch events:', err));
    fetch('/api/aliases').then(r => r.json()).then(setAliases)
      .catch(err => console.error('Failed to fetch aliases:', err));

    const connectControlWs = () => {
      wsControl.current = new WebSocket(wsControlUrl);
      wsControl.current.onopen = () => { setIsConnected(true); setHasConnected(true); setReconnectAt(null); };
      wsControl.current.onclose = () => {
        setIsConnected(false);
        if (!closed) {
          setReconnectAt(Date.now() + 3000);
          setTimeout(connectControlWs, 3000);
        }
      };
      wsControl.current.onmessage = (event) => {
        try {
          // The server re-sends scanner state on a timer, often unchanged. Every applied
          // update re-renders the app, so skip the ones that are byte-for-byte repeats.
          if (typeof event.data === 'string' && event.data.startsWith('{"type":"STATE_UPDATE"')) {
            if (event.data === lastStateRaw.current) return;
            lastStateRaw.current = event.data;
          }
          const message = JSON.parse(event.data);
          if (message.type === 'STATE_UPDATE') {
            const newState = message.payload as ScannerState;
            setScannerState(newState);
          } else if (message.type === 'NEW_LOG') {
            const newEntry = message.payload as CallLog;
            setCallLog(log => {
              if (log.some(x => x.id === newEntry.id)) {
                return log.map(x => x.id === newEntry.id ? newEntry : x);
              }
              return [newEntry, ...log].slice(0, 100);
            });
          } else if (message.type === 'NEW_EVENT') {
            const newEvent = message.payload as RadioEvent;
            setRadioEvents(events => {
              if (events.some(x => x.id === newEvent.id)) return events;
              return [newEvent, ...events].slice(0, 100);
            });
          } else if (message.type === 'UPDATE_PROGRESS') {
            const p = message.payload as UpdateProgress;
            if (p.state) setUpdateState(p.state);
            if (p.line) setUpdateLog(prev => [...prev, p.line]);
          } else if (message.type === 'ERROR') {
            setErrorMsg(message.payload);
          }
        } catch (err) {
          console.warn('Unknown control message or parse error:', event.data, err);
        }
      };
    };

    connectControlWs();
    return () => {
      closed = true;
      wsControl.current?.close();
    };
  }, []);

  return {
    scannerState,
    channels,
    fireTones,
    callLog,
    radioEvents,
    aliases,
    aliasCandidates,
    nameFor,
    refreshAliasCandidates,
    handleSaveAlias,
    handleDeleteAlias,
    importAliases,
    updateLog,
    updateState,
    seedUpdate,
    isConnected,
    hasConnected,
    reconnectAt,
    errorMsg,
    channelsLoaded,
    logLoaded,
    setErrorMsg,
    setCallLog,
    sendCommand,
    handleSkip,
    refreshChannels,
    handleSaveChannel,
    handleDeleteChannel,
    handleSaveFireTone,
    handleDeleteFireTone,
    deleteEntry,
    clearEvents,
  };
}
