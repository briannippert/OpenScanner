import { useCallback, useMemo, useState } from 'react';

export const DIALOG_IDS = [
  'channels', 'fireTones', 'settings', 'rfDebug', 'systemDebug', 'aliases', 'update', 'palette', 'stats',
] as const;
export type DialogId = typeof DIALOG_IDS[number];

interface DialogHandlers {
  open: () => void;
  close: () => void;
  toggle: () => void;
}

interface DialogState {
  open: ReadonlySet<DialogId>;
  /** Every dialog that has been opened at least once this session. */
  opened: ReadonlySet<DialogId>;
}

const EMPTY: DialogState = { open: new Set(), opened: new Set() };

/**
 * Open/closed state for the app's dialogs, with handlers whose identity never changes.
 *
 * The app re-renders on every scanner state update, so handlers created inline would
 * hand every child a new prop each time and defeat memoization. `handlers[id].open` etc.
 * are created once.
 */
export function useDialogs() {
  const [state, setState] = useState<DialogState>(EMPTY);

  const set = useCallback((id: DialogId, next: boolean | 'toggle') => {
    setState(prev => {
      const isOpen = prev.open.has(id);
      const want = next === 'toggle' ? !isOpen : next;
      if (want === isOpen) return prev;
      const open = new Set(prev.open);
      if (want) open.add(id); else open.delete(id);
      const opened = want && !prev.opened.has(id) ? new Set(prev.opened).add(id) : prev.opened;
      return { open, opened };
    });
  }, []);

  const handlers = useMemo(() => {
    const h = {} as Record<DialogId, DialogHandlers>;
    for (const id of DIALOG_IDS) {
      h[id] = {
        open: () => set(id, true),
        close: () => set(id, false),
        toggle: () => set(id, 'toggle'),
      };
    }
    return h;
  }, [set]);

  return {
    isOpen: (id: DialogId) => state.open.has(id),
    wasOpened: (id: DialogId) => state.opened.has(id),
    handlers,
  };
}
