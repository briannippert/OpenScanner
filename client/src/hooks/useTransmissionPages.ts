import { useCallback, useEffect, useRef, useState } from 'react';
import type { CallLog } from '../types';

/** One page from `GET /api/transmissions`, newest first. */
export interface TransmissionPage {
    items: CallLog[];
    /** Pass back as `cursor` for the next (older) page; null on the last page. */
    nextCursor: string | null;
}

export type PageStatus = 'idle' | 'loading' | 'loaded' | 'error';

export const DEFAULT_PAGE_SIZE = 50;

/** Builds a `/api/transmissions` query string from optional filters, skipping empty ones. */
export const transmissionQuery = (params: Record<string, string | number | boolean | null | undefined>): string => {
    const qs = new URLSearchParams();
    for (const [key, value] of Object.entries(params)) {
        if (value === null || value === undefined || value === false || value === '') continue;
        qs.set(key, String(value));
    }
    return qs.toString();
};

interface PagesState {
    /** The filter set these rows belong to. */
    query: string | null;
    items: CallLog[];
    nextCursor: string | null;
    status: PageStatus;
}

const fresh = (query: string | null, auto: boolean): PagesState =>
    ({ query, items: [], nextCursor: null, status: query !== null && auto ? 'loading' : 'idle' });

/**
 * Pages through `GET /api/transmissions` for one filter set. `query` is the filter
 * part of the query string (see {@link transmissionQuery}); `null` disables the hook.
 * Changing `query` drops what was loaded and, when `auto`, fetches its first page.
 * A response for a query that has since changed is ignored.
 */
export function useTransmissionPages(query: string | null, { auto = true, pageSize = DEFAULT_PAGE_SIZE } = {}) {
    const [state, setState] = useState<PagesState>(() => fresh(query, auto));
    const inFlight = useRef<AbortController | null>(null);

    // A new filter set starts over. Adjusting state during render (React's pattern
    // for state derived from a changed prop) means stale rows never render.
    let current = state;
    if (state.query !== query) {
        current = fresh(query, auto);
        setState(current);
    }

    /** Fetches one page; only touches state from the async callbacks. */
    const request = useCallback((cursor: string | null) => {
        if (query === null) return;
        inFlight.current?.abort();
        const controller = new AbortController();
        inFlight.current = controller;

        const qs = [query, `limit=${pageSize}`, cursor ? `cursor=${encodeURIComponent(cursor)}` : '']
            .filter(Boolean)
            .join('&');
        fetch(`/api/transmissions?${qs}`, { signal: controller.signal })
            .then(res => {
                if (res.ok === false) throw new Error(`HTTP ${res.status}`);
                return res.json();
            })
            .then((page: TransmissionPage) => {
                setState(prev => {
                    if (prev.query !== query) return prev;
                    const rows = page.items ?? [];
                    const seen = new Set(cursor ? prev.items.map(l => l.id) : []);
                    return {
                        query,
                        items: cursor ? [...prev.items, ...rows.filter(l => !seen.has(l.id))] : rows,
                        nextCursor: page.nextCursor ?? null,
                        status: 'loaded',
                    };
                });
            })
            .catch(err => {
                if (controller.signal.aborted) return;
                console.error('Failed to load transmissions:', err);
                setState(prev => (prev.query === query ? { ...prev, status: 'error' } : prev));
            });
    }, [query, pageSize]);

    useEffect(() => {
        if (query !== null && auto) request(null);
        return () => { inFlight.current?.abort(); };
    }, [query, auto, request]);

    const { status, items, nextCursor } = current;

    /** Loads the next older page; the first page if nothing has loaded yet (or it failed). */
    const loadMore = useCallback(() => {
        if (status === 'loading') return;
        if (status === 'loaded' && !nextCursor) return; // already at the end
        setState(prev => ({ ...prev, status: 'loading' }));
        // nextCursor only advances on success, so after a failure this retries the same page.
        request(items.length ? nextCursor : null);
    }, [status, nextCursor, items.length, request]);

    /** Drops a row locally, e.g. after it was deleted. */
    const remove = useCallback((id: string) => {
        setState(prev => ({ ...prev, items: prev.items.filter(l => l.id !== id) }));
    }, []);

    return {
        items,
        status,
        /** True when the server said older matching rows remain. */
        hasMore: nextCursor !== null,
        loadMore,
        remove,
    };
}
