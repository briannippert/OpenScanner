import { render as rtlRender, screen, fireEvent } from '@testing-library/react';
import { ThemeProvider } from '@mui/material/styles';
import type { ReactElement } from 'react';
import { describe, it, expect, vi } from 'vitest';
import RadiosSection from './RadiosSection';
import { planSummary, roleSummary } from '../lib/radios';
import theme from '../theme/theme';
import type { Radio, RadiosResponse, ScannerState } from '../types';

const render = (ui: ReactElement) => rtlRender(<ThemeProvider theme={theme}>{ui}</ThemeProvider>);

const radio = (id: string, label: string, extra: Partial<Radio> = {}): Radio => ({
  id, label, serial: id.replace('sn:', ''), product: 'RTL2838UHIDIR', attached: true, index: 0,
  serialIsUnique: true, enabled: true, ppm: 0, gainAnalog: 40, gainDigital: 30, gainMixed: 35, role: 'FastScan',
  ...extra,
});

const response = (): RadiosResponse => ({
  mode: 'auto',
  devices: [radio('sn:A', 'Roof'), radio('sn:B', 'Attic', { role: 'Hop', index: 1 })],
  plan: {
    totalChannels: 5,
    fastScanned: 3,
    scanned: 5,
    devices: [
      { deviceId: 'sn:A', label: 'Roof', role: 'FastScan', gainClass: 'Digital', gain: 30, centerFrequency: 155.2, spanMHz: 0.4, frequencies: [155.0, 155.2, 155.4], channelIds: [1, 2, 3] },
      { deviceId: 'sn:B', label: 'Attic', role: 'Hop', gainClass: 'Analog', gain: 40, spanMHz: 300, frequencies: [460.0, 851.0], channelIds: [4, 5] },
    ],
    autoPlacedChannelIds: [],
    unscannedChannelIds: [],
  },
  warnings: [],
});

const renderSection = (overrides: Partial<React.ComponentProps<typeof RadiosSection>> = {}) => {
  const props = {
    radios: response(),
    error: null,
    onSetMode: vi.fn(),
    onUpdate: vi.fn(),
    onRescan: vi.fn(),
    ...overrides,
  };
  render(<RadiosSection {...props} />);
  return props;
};

describe('planSummary', () => {
  it('counts fast-scanned channels and names the hopper', () => {
    expect(planSummary(response().plan)).toBe('3 of 5 channels fast-scanned across 2 SDRs · 2 hopping on Attic');
  });

  it('reports unscanned channels', () => {
    const plan = { ...response().plan, unscannedChannelIds: [9] };
    expect(planSummary(plan)).toContain('1 not scanned');
  });
});

describe('roleSummary', () => {
  it('describes a fast-scan window by its range', () => {
    expect(roleSummary(response().plan.devices[0])).toBe('Fast scan 155.000–155.400 MHz · 3 channels');
  });

  it('describes an idle SDR', () => {
    expect(roleSummary(undefined)).toMatch(/^Idle/);
  });
});

describe('RadiosSection', () => {
  it('shows a card per dongle with what it is doing', () => {
    renderSection();
    expect(screen.getByDisplayValue('Roof')).toBeInTheDocument();
    expect(screen.getByDisplayValue('Attic')).toBeInTheDocument();
    expect(screen.getByText(/Hopping · 2 channels/)).toBeInTheDocument();
    expect(screen.getByTestId('plan-summary')).toHaveTextContent('3 of 5');
  });

  it('switches mode', () => {
    const props = renderSection();
    fireEvent.click(screen.getByRole('button', { name: 'Manual' }));
    expect(props.onSetMode).toHaveBeenCalledWith('manual');
  });

  /** Each save restarts the SDR, so a gain is only saved when the edit is finished. */
  it('saves a gain on blur, not on every keystroke', () => {
    const props = renderSection();
    const digital = screen.getAllByLabelText('Digital dB')[0];
    fireEvent.change(digital, { target: { value: '28' } });
    expect(props.onUpdate).not.toHaveBeenCalled();

    fireEvent.blur(digital);
    expect(props.onUpdate).toHaveBeenCalledWith('sn:A', { gainDigital: 28 });
  });

  it('rejects an out-of-range gain', () => {
    const props = renderSection();
    const analog = screen.getAllByLabelText('Analog dB')[0];
    fireEvent.change(analog, { target: { value: '99' } });
    fireEvent.blur(analog);
    expect(props.onUpdate).not.toHaveBeenCalled();
    expect(analog).toHaveValue(40);
  });

  it('offers the ppm measured on that dongle only', () => {
    const scannerState = {
      status: 'SCANNING', signalStrength: 0,
      parallelChannels: [{
        channel: { frequency: 155.0, alphaTag: 'Fire' }, isActive: true, signalStrength: -40,
        isRecording: false, measuredOffsetHz: 1550, deviceId: 'sn:A',
      }],
    } as unknown as ScannerState;
    const props = renderSection({ scannerState });

    // The tooltip names the button after the measurement; its text is the action.
    const apply = screen.getByText('Apply 10.0 ppm');
    expect(screen.getAllByText(/^Apply /)).toHaveLength(1);
    expect(screen.getByRole('button', { name: 'Measured on Fire: 1.55 kHz off' })).toBe(apply.closest('button'));
    fireEvent.click(apply);
    expect(props.onUpdate).toHaveBeenCalledWith('sn:A', { ppm: 10 });
  });

  it('marks an unplugged dongle', () => {
    const radios = response();
    radios.devices[1] = { ...radios.devices[1], attached: false };
    renderSection({ radios });
    expect(screen.getByText('UNPLUGGED')).toBeInTheDocument();
  });

  it('shows warnings', () => {
    const radios = { ...response(), warnings: ['Some SDRs share a serial number'] };
    renderSection({ radios });
    expect(screen.getByText('Some SDRs share a serial number')).toBeInTheDocument();
  });
});
