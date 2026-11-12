import { useCallback, useEffect, useState } from 'react';
import { apiFetch, apiJson } from '../components/common/apiBase';
import type { AssignmentMode, Radio, RadiosResponse, SdrPlan } from '../types';

/** Editable dongle settings; omitted fields are unchanged. */
export type RadioPatch = Partial<Pick<Radio, 'label' | 'enabled' | 'ppm' | 'gainAnalog' | 'gainDigital' | 'gainMixed'>>;

const isRadiosResponse = (v: unknown): v is RadiosResponse =>
    !!v && typeof v === 'object' && Array.isArray((v as RadiosResponse).devices) && !!(v as RadiosResponse).plan;

const jsonInit = (method: string, body: unknown): RequestInit => ({
    method,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
});

/**
 * The SDR dongles, the assignment mode and the channel plan, loaded while `active`.
 * Every write re-reads the result, since a change to one dongle or one pin can move
 * channels on the others.
 */
export const useRadios = (active: boolean) => {
    const [radios, setRadios] = useState<RadiosResponse | null>(null);
    const [error, setError] = useState<string | null>(null);

    const refresh = useCallback(async () => {
        const data = await apiJson<unknown>('/api/radios');
        if (isRadiosResponse(data)) setRadios(data);
    }, []);

    useEffect(() => {
        if (!active) return;
        let cancelled = false;
        apiJson<unknown>('/api/radios').then(data => {
            if (!cancelled && isRadiosResponse(data)) setRadios(data);
        });
        return () => { cancelled = true; };
    }, [active]);

    const write = useCallback(async (what: string, path: string, init: RequestInit) => {
        try {
            const res = await apiFetch(path, init);
            if (!res.ok) {
                const text = await res.text().catch(() => '');
                setError(text.replace(/^"|"$/g, '') || `Could not ${what}`);
                return false;
            }
            setError(null);
            const body = res.status === 204 ? null : await res.json().catch(() => null);
            if (isRadiosResponse(body)) setRadios(body);
            else await refresh();
            return true;
        } catch {
            setError(`Could not ${what}`);
            return false;
        }
    }, [refresh]);

    const rescan = useCallback(() => write('rescan', '/api/radios/rescan', { method: 'POST' }), [write]);

    const setMode = useCallback(
        (mode: AssignmentMode) => write('change the mode', '/api/radios/mode', jsonInit('PUT', { mode })),
        [write]);

    const updateRadio = useCallback(
        (id: string, patch: RadioPatch) =>
            write('save the SDR settings', `/api/radios/${encodeURIComponent(id)}`, jsonInit('PUT', patch)),
        [write]);

    const assign = useCallback(
        (channelIds: number[], deviceId: string | null) =>
            write('assign the channel', '/api/radios/assignments', jsonInit('PUT', { channelIds, deviceId })),
        [write]);

    const preview = useCallback(
        (mode: AssignmentMode, pins: Record<number, string | null>) =>
            apiJson<SdrPlan>('/api/radios/plan/preview', jsonInit('POST', { mode, pins })),
        []);

    return { radios, error, refresh, rescan, setMode, updateRadio, assign, preview };
};
