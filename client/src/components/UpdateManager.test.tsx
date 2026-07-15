import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import UpdateManager from './UpdateManager';
import type { UpdateStatus } from '../types';

const fetchMock = vi.fn();
vi.stubGlobal('fetch', fetchMock);

const status = (over: Partial<UpdateStatus> = {}): UpdateStatus => ({
    state: 'idle',
    currentVersion: '0.1.70',
    currentCommit: 'abcdef1234567890',
    commitsBehind: 0,
    updateAvailable: false,
    log: [],
    lastCheckedUtc: new Date(Date.now() - 25 * 60_000).toISOString(),
    ...over,
});

const jsonOnce = (body: unknown) =>
    fetchMock.mockResolvedValueOnce({ ok: true, json: async () => body });

const renderDialog = (props: Partial<React.ComponentProps<typeof UpdateManager>> = {}) =>
    render(
        <UpdateManager
            open
            onClose={vi.fn()}
            log={[]}
            state="idle"
            onSeed={vi.fn()}
            {...props}
        />,
    );

describe('UpdateManager — manual check', () => {
    beforeEach(() => fetchMock.mockReset());

    it('shows when the last check happened, so the 30-minute poll is visible', async () => {
        jsonOnce(status());
        renderDialog();
        expect(await screen.findByText('Checked 25 minutes ago')).toBeDefined();
    });

    it('posts to /api/update/check and surfaces a newly-found release', async () => {
        jsonOnce(status()); // initial GET /api/update/status
        renderDialog();
        await screen.findByText(/Up to date/);

        // The forced check finds v0.2.0.
        jsonOnce(status({
            state: 'available',
            updateAvailable: true,
            latestTag: 'v0.2.0',
            commitsBehind: 4,
            lastCheckedUtc: new Date().toISOString(),
        }));

        fireEvent.click(screen.getByRole('button', { name: /Check now/ }));

        await waitFor(() => {
            const call = fetchMock.mock.calls.find(c => String(c[0]).includes('/api/update/check'));
            expect(call).toBeDefined();
            expect(call?.[1]?.method).toBe('POST');
        });

        // The primary action flips from "Up to date" to offering the new release.
        expect(await screen.findByRole('button', { name: /Update to v0\.2\.0/ })).toBeDefined();
        expect(screen.getByText(/latest v0\.2\.0 \(4 behind\)/)).toBeDefined();
        expect(screen.getByText('Checked just now')).toBeDefined();
    });

    it('surfaces a failed check and re-enables the button', async () => {
        jsonOnce(status());
        renderDialog();
        await screen.findByText(/Up to date/);

        jsonOnce(status({ error: 'Update check failed: GitHub returned 503' }));
        fireEvent.click(screen.getByRole('button', { name: /Check now/ }));

        expect(await screen.findByText(/GitHub returned 503/)).toBeDefined();
        await waitFor(() =>
            expect(screen.getByRole('button', { name: /Check now/ }).hasAttribute('disabled')).toBe(false),
        );
    });

    it('does not leave the button stuck spinning if the request throws', async () => {
        jsonOnce(status());
        renderDialog();
        await screen.findByText(/Up to date/);

        fetchMock.mockRejectedValueOnce(new Error('network down'));
        fireEvent.click(screen.getByRole('button', { name: /Check now/ }));

        await waitFor(() =>
            expect(screen.getByRole('button', { name: /Check now/ }).hasAttribute('disabled')).toBe(false),
        );
    });

    it('is disabled mid-update so a check cannot race the build', async () => {
        jsonOnce(status());
        renderDialog({ state: 'updating' });
        await waitFor(() =>
            expect(screen.getByRole('button', { name: /Check now/ }).hasAttribute('disabled')).toBe(true),
        );
    });
});
