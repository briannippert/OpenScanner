import { renderHook, waitFor, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { transmissionQuery, useTransmissionPages } from './useTransmissionPages';
import type { CallLog } from '../types';

const fetchMock = vi.fn();
vi.stubGlobal('fetch', fetchMock);

const row = (id: string): CallLog =>
    ({ id, timestamp: '2026-11-01T12:00:00Z', frequency: 155, alphaTag: 'Tag', description: '' });

const respond = (items: CallLog[], nextCursor: string | null) =>
    Promise.resolve({ ok: true, json: async () => ({ items, nextCursor }) });

describe('transmissionQuery', () => {
    it('skips empty and false values', () => {
        expect(transmissionQuery({ q: 'fire & rescue', tones: true, favorites: false, channel: '', until: undefined }))
            .toBe('q=fire+%26+rescue&tones=true');
    });
});

describe('useTransmissionPages', () => {
    beforeEach(() => { fetchMock.mockReset(); });

    it('loads the first page, then follows the cursor until the end', async () => {
        fetchMock
            .mockImplementationOnce(() => respond([row('a'), row('b')], 'CUR1'))
            .mockImplementationOnce(() => respond([row('c')], null));

        const { result } = renderHook(() => useTransmissionPages('favorites=true', { pageSize: 2 }));

        await waitFor(() => expect(result.current.status).toBe('loaded'));
        expect(fetchMock).toHaveBeenLastCalledWith('/api/transmissions?favorites=true&limit=2', expect.anything());
        expect(result.current.items.map(l => l.id)).toEqual(['a', 'b']);
        expect(result.current.hasMore).toBe(true);

        act(() => result.current.loadMore());
        await waitFor(() => expect(result.current.items).toHaveLength(3));
        expect(fetchMock).toHaveBeenLastCalledWith('/api/transmissions?favorites=true&limit=2&cursor=CUR1', expect.anything());
        expect(result.current.hasMore).toBe(false);

        // At the end, loadMore does nothing.
        act(() => result.current.loadMore());
        expect(fetchMock).toHaveBeenCalledTimes(2);
    });

    it('starts over when the query changes and ignores the old response', async () => {
        let releaseOld: () => void = () => {};
        fetchMock.mockImplementation((url: string) => {
            if (url.includes('q=old')) {
                return new Promise(resolve => { releaseOld = () => resolve({ ok: true, json: async () => ({ items: [row('old')], nextCursor: null }) }); });
            }
            return respond([row('new')], null);
        });

        const { result, rerender } = renderHook(({ q }) => useTransmissionPages(`q=${q}`), { initialProps: { q: 'old' } });
        rerender({ q: 'new' });
        await waitFor(() => expect(result.current.items.map(l => l.id)).toEqual(['new']));

        await act(async () => { releaseOld(); });
        expect(result.current.items.map(l => l.id)).toEqual(['new']);
    });

    it('reports a failure and retries the same page', async () => {
        fetchMock
            .mockImplementationOnce(() => respond([row('a')], 'CUR1'))
            .mockImplementationOnce(() => Promise.resolve({ ok: false, status: 500, json: async () => ({}) }))
            .mockImplementationOnce(() => respond([row('b')], null));
        vi.spyOn(console, 'error').mockImplementation(() => {});

        const { result } = renderHook(() => useTransmissionPages('tones=true'));
        await waitFor(() => expect(result.current.status).toBe('loaded'));

        act(() => result.current.loadMore());
        await waitFor(() => expect(result.current.status).toBe('error'));
        expect(result.current.items.map(l => l.id)).toEqual(['a']);

        act(() => result.current.loadMore());
        await waitFor(() => expect(result.current.status).toBe('loaded'));
        expect(fetchMock).toHaveBeenLastCalledWith(expect.stringContaining('cursor=CUR1'), expect.anything());
        expect(result.current.items.map(l => l.id)).toEqual(['a', 'b']);
    });

    it('does nothing while disabled', () => {
        const { result } = renderHook(() => useTransmissionPages(null));
        expect(result.current.status).toBe('idle');
        expect(fetchMock).not.toHaveBeenCalled();
    });

    it('removes a row locally', async () => {
        fetchMock.mockImplementation(() => respond([row('a'), row('b')], null));
        const { result } = renderHook(() => useTransmissionPages('favorites=true'));
        await waitFor(() => expect(result.current.items).toHaveLength(2));

        act(() => result.current.remove('a'));

        expect(result.current.items.map(l => l.id)).toEqual(['b']);
    });
});
