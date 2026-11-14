import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import EventLog from './EventLog';
import type { RadioEvent } from '../types';

const fetchMock = vi.fn();
vi.stubGlobal('fetch', fetchMock);

const event = (id: string, transmissionId: string): RadioEvent =>
    ({ id, timestamp: '2026-11-01T12:00:00Z', type: 'TONE_OUT', label: `Station ${id}`, frequency: 154.43, transmissionId });

describe('EventLog', () => {
    it('fetches transcriptions for older tone-outs in one batch request', async () => {
        fetchMock.mockResolvedValue({
            ok: true,
            json: async () => ({ items: [{ id: 'tx1', transcription: 'Engine 2 respond' }], nextCursor: null }),
        });
        const events = [event('1', 'tx1'), event('2', 'tx2'), event('3', 'live')];

        render(<EventLog events={events} onClear={() => {}} transcriptions={new Map([['live', 'Already here']])} />);

        expect(await screen.findByText('“Engine 2 respond”')).toBeInTheDocument();
        expect(fetchMock).toHaveBeenCalledTimes(1);
        expect(fetchMock).toHaveBeenCalledWith('/api/transmissions?ids=tx1,tx2');
    });
});
