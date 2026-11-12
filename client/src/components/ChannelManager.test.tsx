import { render as rtlRender, screen, fireEvent, within, waitFor } from '@testing-library/react';
import { ThemeProvider } from '@mui/material/styles';
import type { ReactElement } from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import ChannelManager from './ChannelManager';
import theme from '../theme/theme';
import type { Channel, RadiosResponse } from '../types';

const fetchMock = vi.fn();
vi.stubGlobal('fetch', fetchMock);

const render = (ui: ReactElement) => rtlRender(<ThemeProvider theme={theme}>{ui}</ThemeProvider>);

const ch = (id: number, frequency: number, alphaTag: string): Channel => ({
  id, frequency, alphaTag, description: '', license: '', type: 'RM', tone: '', mode: 'NFM', tag: '', avoid: false,
});

const channels = [ch(1, 155.0, 'Fire'), ch(2, 460.0, 'Police')];

const radios = (mode: 'auto' | 'manual'): RadiosResponse => ({
  mode,
  devices: ['A', 'B'].map((x, i) => ({
    id: `sn:${x}`, label: `SDR ${x}`, serial: x, product: 'RTL', attached: true, index: i, serialIsUnique: true,
    enabled: true, ppm: 0, gainAnalog: 0, gainDigital: 0, gainMixed: 0, role: 'FastScan' as const,
  })),
  plan: {
    totalChannels: 2, fastScanned: 2, scanned: 2,
    devices: [
      { deviceId: 'sn:A', label: 'SDR A', role: 'FastScan', gainClass: 'Analog', gain: 0, spanMHz: 0, frequencies: [155.0], channelIds: [1] },
      { deviceId: 'sn:B', label: 'SDR B', role: 'FastScan', gainClass: 'Analog', gain: 0, spanMHz: 0, frequencies: [460.0], channelIds: [2] },
    ],
    autoPlacedChannelIds: mode === 'manual' ? [2] : [],
    unscannedChannelIds: [],
  },
  warnings: [],
});

const respond = (body: unknown) => ({ ok: true, status: 200, json: async () => body, text: async () => '' });

const renderManager = () =>
  render(<ChannelManager open onClose={() => {}} channels={channels} onSave={() => {}} onDelete={() => {}} />);

describe('ChannelManager SDR placement', () => {
  beforeEach(() => vi.clearAllMocks());

  it('shows where Auto mode put each channel', async () => {
    fetchMock.mockResolvedValue(respond(radios('auto')));
    renderManager();

    expect(await screen.findByText('SDR A · fast')).toBeInTheDocument();
    expect(screen.getByText('SDR B · fast')).toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: /SDR for/ })).not.toBeInTheDocument();
  });

  it('pins a channel in Manual mode', async () => {
    fetchMock.mockResolvedValue(respond(radios('manual')));
    renderManager();

    const picker = await screen.findByRole('combobox', { name: 'SDR for Fire' });
    // Police was left on Auto and the planner placed it.
    expect(screen.getByText('AUTO → SDR B')).toBeInTheDocument();

    fireEvent.mouseDown(picker);
    fireEvent.click(within(screen.getByRole('listbox')).getByText('SDR B'));

    await waitFor(() => {
      const call = fetchMock.mock.calls.find(([url, init]) =>
        String(url).endsWith('/api/radios/assignments') && init?.method === 'PUT');
      expect(call).toBeDefined();
      expect(JSON.parse(call![1].body)).toEqual({ channelIds: [1], deviceId: 'sn:B' });
    });
  });

  it('hides the column with a single dongle', async () => {
    const one = radios('manual');
    one.devices = one.devices.slice(0, 1);
    fetchMock.mockResolvedValue(respond(one));
    renderManager();

    await screen.findByText('Fire');
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(screen.queryByRole('combobox', { name: /SDR for/ })).not.toBeInTheDocument();
  });
});
