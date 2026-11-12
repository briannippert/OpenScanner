import { useEffect, useRef, useState } from 'react';
import type { CallLog } from '../types';

/**
 * Listens on the control WebSocket for NEW_LOG messages only, and reconnects when the
 * connection drops. A lighter alternative to useScannerSocket for pages that need the
 * live call feed but no scanner state or controls.
 *
 * @returns whether the socket is connected.
 */
export function useLiveLogs(onLog: (log: CallLog) => void): boolean {
  const [connected, setConnected] = useState(false);
  const onLogRef = useRef(onLog);
  useEffect(() => { onLogRef.current = onLog; }, [onLog]);

  useEffect(() => {
    const wsProtocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const url = `${wsProtocol}//${window.location.host}/ws/control`;
    let closed = false;
    let ws: WebSocket | null = null;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const connect = () => {
      ws = new WebSocket(url);
      ws.onopen = () => setConnected(true);
      ws.onclose = () => {
        setConnected(false);
        if (!closed) timer = setTimeout(connect, 3000);
      };
      ws.onmessage = (event) => {
        if (typeof event.data !== 'string') return;
        try {
          const message = JSON.parse(event.data) as { type?: string; payload?: unknown };
          if (message.type === 'NEW_LOG' && message.payload) onLogRef.current(message.payload as CallLog);
        } catch {
          // Not JSON we understand; ignore.
        }
      };
    };
    connect();

    return () => {
      closed = true;
      if (timer) clearTimeout(timer);
      ws?.close();
    };
  }, []);

  return connected;
}
