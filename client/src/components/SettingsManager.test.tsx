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

describe('SettingsManager AI call analysis', () => {
  const remoteSettings = {
    EnableTranscription: 'true',
    TranscriptionBackend: 'remote',
    RemoteTranscriptionUrl: 'http://whisper.test:8099',
    AiFeaturesEnabled: 'false',
  };

  const respond = (supported: boolean) => (url: string) => {
    const body =
      url.endsWith('/api/settings') ? remoteSettings
      : url.includes('/api/ai/capabilities') ? { supported, enabled: false }
      : url.includes('/api/transcription/remote/models') ? []
      : {};
    return Promise.resolve({ ok: true, json: async () => body });
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('offers the AI toggle when the remote server supports /transmission', async () => {
    fetchMock.mockImplementation(respond(true));
    render(<SettingsManager open onClose={() => {}} />);

    expect(await screen.findByText('AI call analysis', {}, { timeout: 3000 })).toBeInTheDocument();
    const probe = fetchMock.mock.calls.map(c => String(c[0])).find(u => u.includes('/api/ai/capabilities'));
    expect(probe).toContain(encodeURIComponent('http://whisper.test:8099'));
  });

  it('hides the AI toggle when the remote server does not support it', async () => {
    fetchMock.mockImplementation(respond(false));
    render(<SettingsManager open onClose={() => {}} />);

    await screen.findAllByText('Server URL');
    // Wait past the debounced probe.
    await new Promise(r => setTimeout(r, 800));
    expect(fetchMock.mock.calls.some(c => String(c[0]).includes('/api/ai/capabilities'))).toBe(true);
    expect(screen.queryByText('AI call analysis')).not.toBeInTheDocument();
  });
});
