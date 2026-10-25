import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  detectAudioCodec,
  parseAudioFormat,
  resetCodecDetection,
  wasmDecoderLoadCount,
} from './opusStream';

vi.mock('opus-decoder', () => ({
  OpusDecoder: class {
    ready = Promise.resolve();
    decodeFrame() {
      return { channelData: [new Float32Array(960)], samplesDecoded: 960, sampleRate: 48000, errors: [] };
    }
    reset() { return Promise.resolve(); }
    free() { /* noop */ }
  },
}));

function stubAudioDecoder(supported: boolean | 'absent') {
  if (supported === 'absent') {
    vi.stubGlobal('AudioDecoder', undefined);
    return;
  }
  vi.stubGlobal('AudioDecoder', {
    isConfigSupported: vi.fn().mockResolvedValue({ supported }),
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
  resetCodecDetection();
});

describe('parseAudioFormat', () => {
  it('reads the opus announcement', () => {
    const fmt = parseAudioFormat(
      JSON.stringify({
        type: 'AUDIO_FORMAT',
        payload: { codec: 'opus', sampleRate: 48000, channels: 2, frameMs: 20, bitrate: 40000 },
      }),
    );
    expect(fmt).toEqual({ codec: 'opus', sampleRate: 48000, channels: 2, frameMs: 20, bitrate: 40000 });
  });

  it('normalizes the server pcm codec name', () => {
    const fmt = parseAudioFormat(
      JSON.stringify({ type: 'AUDIO_FORMAT', payload: { codec: 'pcm_s16le', sampleRate: 48000, channels: 1 } }),
    );
    expect(fmt?.codec).toBe('pcm');
    expect(fmt?.channels).toBe(1);
  });

  it('ignores other message types and malformed input', () => {
    expect(parseAudioFormat(JSON.stringify({ type: 'STATE_UPDATE', payload: {} }))).toBeNull();
    expect(parseAudioFormat('not json')).toBeNull();
    expect(
      parseAudioFormat(JSON.stringify({ type: 'AUDIO_FORMAT', payload: { codec: 'flac' } })),
    ).toBeNull();
  });
});

describe('detectAudioCodec', () => {
  it('prefers WebCodecs and never downloads the wasm decoder', async () => {
    stubAudioDecoder(true);

    await expect(detectAudioCodec()).resolves.toBe('opus');
    // The bundle-size guarantee: browsers on the native path must not pay for libopus.
    expect(wasmDecoderLoadCount()).toBe(0);
  });

  it('falls back to the wasm decoder when WebCodecs is missing', async () => {
    stubAudioDecoder('absent');

    await expect(detectAudioCodec()).resolves.toBe('opus');
    expect(wasmDecoderLoadCount()).toBe(1);
  });

  it('falls back to the wasm decoder when WebCodecs rejects opus', async () => {
    stubAudioDecoder(false);

    await expect(detectAudioCodec()).resolves.toBe('opus');
    expect(wasmDecoderLoadCount()).toBe(1);
  });

  it('memoizes, so reconnects do not re-probe', async () => {
    stubAudioDecoder(true);

    await detectAudioCodec();
    await detectAudioCodec();

    const decoder = globalThis.AudioDecoder as unknown as { isConfigSupported: ReturnType<typeof vi.fn> };
    expect(decoder.isConfigSupported).toHaveBeenCalledTimes(1);
  });
});
