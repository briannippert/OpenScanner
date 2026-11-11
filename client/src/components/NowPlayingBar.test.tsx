import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import NowPlayingBar from './NowPlayingBar';
import { ThemeModeProvider } from '../theme/ThemeModeProvider';
import type { NowPlaying } from '../hooks/useAudioPipeline';

const clip: NowPlaying = { id: '1', filename: 'a.wav', label: 'Police Dispatch', duration: 100, peaks: [0.2, 0.8, 0.5] };

const renderBar = (over: Partial<React.ComponentProps<typeof NowPlayingBar>> = {}) => {
  const props = {
    nowPlaying: clip,
    isPaused: false,
    getPosition: () => 50,
    playbackRate: 1,
    onTogglePause: vi.fn(),
    onSeek: vi.fn(),
    onSetRate: vi.fn(),
    onStop: vi.fn(),
    ...over,
  };
  // The bar reads custom tokens (mono font, surface colours) from the app theme.
  render(<ThemeModeProvider><NowPlayingBar {...props} /></ThemeModeProvider>);
  return props;
};

describe('NowPlayingBar', () => {
  it('reads the position from getPosition rather than a prop', async () => {
    renderBar({ getPosition: () => 65 });
    // Polled on an animation frame, so it appears shortly after mount.
    expect(await screen.findByText('1:05 / 1:40')).toBeInTheDocument();
  });

  it('seeks with the keyboard from the scrubber', async () => {
    const { onSeek } = renderBar();
    await screen.findByText('0:50 / 1:40');
    const slider = screen.getByRole('slider', { name: 'Seek' });
    expect(slider).toHaveAttribute('tabindex', '0');

    const lastSeek = () => (onSeek as ReturnType<typeof vi.fn>).mock.lastCall?.[0] as number;
    fireEvent.keyDown(slider, { key: 'ArrowRight' });
    expect(lastSeek()).toBeCloseTo(55);
    fireEvent.keyDown(slider, { key: 'ArrowLeft' });
    expect(lastSeek()).toBeCloseTo(45);
    fireEvent.keyDown(slider, { key: 'Home' });
    expect(lastSeek()).toBe(0);
    fireEvent.keyDown(slider, { key: 'End' });
    expect(lastSeek()).toBe(100);
  });
});
