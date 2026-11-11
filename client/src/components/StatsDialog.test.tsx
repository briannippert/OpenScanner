import { render as rtlRender, screen, fireEvent, waitFor } from '@testing-library/react';
import { ThemeProvider } from '@mui/material/styles';
import type { ReactElement } from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import StatsDialog from './StatsDialog';
import type { TransmissionStats } from '../lib/stats';
import theme from '../theme/theme';

// The page reads custom tokens (mono font, surface colors) from the app theme.
const render = (ui: ReactElement) => rtlRender(<ThemeProvider theme={theme}>{ui}</ThemeProvider>);

const fetchMock = vi.fn();
vi.stubGlobal('fetch', fetchMock);

const stats = (days: number, over: Partial<TransmissionStats> = {}): TransmissionStats => ({
  days,
  summary: { transmissions: 1200, airtimeSeconds: 7200, longestSeconds: 48, transcribed: 900, toneOuts: 3, activeChannels: 2 },
  channels: [
    { alphaTag: 'County Fire', frequency: 154.43, count: 800, airtimeSeconds: 5000 },
    { alphaTag: '', frequency: 155.0325, count: 400, airtimeSeconds: 2200 },
  ],
  talkgroups: [{ alphaTag: 'County Fire', frequency: 154.43, id: 4021, count: 500, airtimeSeconds: 3000 }],
  sources: [{ alphaTag: 'County Fire', frequency: 154.43, id: 101, count: 90, airtimeSeconds: 600 }],
  tones: [{ name: 'Station 4', count: 3 }],
  ...over,
});

const json = (body: unknown) => ({ ok: true, json: async () => body });

/** Answer each endpoint by URL; `byDays` supplies the stats for a requested window. */
const serve = (byDays: (days: number) => TransmissionStats | null) => {
  fetchMock.mockImplementation(async (url: string) => {
    if (url.includes('/api/history/stats')) {
      const body = byDays(Number(new URL(url, 'http://x').searchParams.get('days')));
      return body ? json(body) : { ok: false, json: async () => ({}) };
    }
    if (url.includes('/api/history/activity')) return json([{ hour: new Date().toISOString(), count: 5 }]);
    return json([]);
  });
};

describe('StatsDialog', () => {
  beforeEach(() => { vi.clearAllMocks(); });

  it('shows headline figures and ranked lists for the default 7-day window', async () => {
    serve(days => stats(days));
    render(<StatsDialog open onClose={() => {}} />);

    expect(await screen.findByText('1,200')).toBeInTheDocument();     // transmissions
    expect(screen.getByText('2h 00m')).toBeInTheDocument();           // airtime
    expect(screen.getByText('75%')).toBeInTheDocument();              // transcribed
    // Named in the channel list and beside its talkgroup and radio.
    expect(screen.getAllByText('County Fire').length).toBeGreaterThanOrEqual(1);
    // A channel with no name falls back to its frequency.
    expect(screen.getByText('155.0325 MHz')).toBeInTheDocument();
    expect(screen.getByText('Station 4')).toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([u]) => String(u).includes('/api/history/stats?days=7'))).toBe(true);
  });

  it('uses alias names for talkgroups and radios, falling back to the raw ID', async () => {
    serve(days => stats(days));
    const nameFor = vi.fn((kind: string, value: number | null | undefined) => (kind === 'TG' && value === 4021 ? 'Fire Dispatch' : undefined));
    render(<StatsDialog open onClose={() => {}} nameFor={nameFor} />);

    expect(await screen.findByText('Fire Dispatch')).toBeInTheDocument();
    expect(screen.getByText('101')).toBeInTheDocument();
    expect(nameFor).toHaveBeenCalledWith('TG', 4021, 'County Fire', 154.43);
  });

  it('reloads when the time range changes', async () => {
    serve(days => stats(days, { summary: { ...stats(days).summary, transmissions: days === 1 ? 48 : 1200 } }));
    render(<StatsDialog open onClose={() => {}} />);
    await screen.findByText('1,200');

    fireEvent.click(screen.getByRole('tab', { name: '24 hours' }));

    expect(await screen.findByText('48')).toBeInTheDocument();
    expect(screen.getByText('2 per hour')).toBeInTheDocument();
    expect(screen.getByText('Transmissions per hour')).toBeInTheDocument();
  });

  it('says so when nothing was recorded', async () => {
    serve(days => stats(days, { summary: { transmissions: 0, airtimeSeconds: 0, longestSeconds: 0, transcribed: 0, toneOuts: 0, activeChannels: 0 }, channels: [], talkgroups: [], sources: [], tones: [] }));
    render(<StatsDialog open onClose={() => {}} />);

    expect(await screen.findByText('Nothing recorded yet')).toBeInTheDocument();
  });

  it('reports a failed load instead of showing zeros', async () => {
    serve(() => null);
    render(<StatsDialog open onClose={() => {}} />);

    expect(await screen.findByText(/Could not load statistics/)).toBeInTheDocument();
    expect(screen.queryByText('Transmissions')).not.toBeInTheDocument();
  });

  it('does not fetch while closed', async () => {
    serve(days => stats(days));
    render(<StatsDialog open={false} onClose={() => {}} />);
    await waitFor(() => expect(fetchMock).not.toHaveBeenCalled());
  });
});
