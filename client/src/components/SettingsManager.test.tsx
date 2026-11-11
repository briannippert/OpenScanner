import { render as rtlRender, screen, fireEvent, within } from '@testing-library/react';
import { ThemeProvider } from '@mui/material/styles';
import type { ReactElement } from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import SettingsManager from './SettingsManager';
import theme from '../theme/theme';

const fetchMock = vi.fn();
vi.stubGlobal('fetch', fetchMock);

const render = (ui: ReactElement) => rtlRender(<ThemeProvider theme={theme}>{ui}</ThemeProvider>);

/** The row (label + description + control) that holds the given setting label. */
const rowFor = (label: string) => screen.getByText(label).parentElement!.parentElement!;

describe('SettingsManager diagnostics', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    fetchMock.mockResolvedValue({ ok: true, json: async () => ({}) });
  });

  it('opens System Debug after closing Settings', async () => {
    const calls: string[] = [];
    render(
      <SettingsManager
        open
        onClose={() => calls.push('close settings')}
        onOpenSystemDebug={() => calls.push('open system debug')}
        onOpenRfDebug={() => calls.push('open rf debug')}
      />,
    );

    await screen.findByText('Diagnostics');
    fireEvent.click(within(rowFor('System Debug')).getByRole('button', { name: 'Open' }));

    // Settings must get out of the way first: the tool is a dialog of its own.
    expect(calls).toEqual(['close settings', 'open system debug']);
  });

  it('opens RF Spectrum Debug after closing Settings', async () => {
    const calls: string[] = [];
    render(
      <SettingsManager
        open
        onClose={() => calls.push('close settings')}
        onOpenSystemDebug={() => calls.push('open system debug')}
        onOpenRfDebug={() => calls.push('open rf debug')}
      />,
    );

    await screen.findByText('Diagnostics');
    fireEvent.click(within(rowFor('RF Spectrum Debug')).getByRole('button', { name: 'Open' }));

    expect(calls).toEqual(['close settings', 'open rf debug']);
  });

  it('leaves the section out when no tools are wired up', async () => {
    render(<SettingsManager open onClose={() => {}} />);

    await screen.findByText('Storage');
    expect(screen.queryByText('Diagnostics')).not.toBeInTheDocument();
  });
});
