/**
 * Opus decoding for the live audio WebSocket.
 *
 * The server streams raw s16le PCM at 48 kHz — 768 kbps mono, 1.5 Mbps stereo — which makes
 * listening from outside the LAN impractical. It will instead send Opus (~24 kbps) to any client
 * that asks for it, so this module answers two questions: can this browser decode Opus, and how.
 *
 * Three tiers, best first:
 *   1. WebCodecs `AudioDecoder` — native, off the main thread, no download. Needs a secure context.
 *   2. `opus-decoder` (libopus compiled to wasm) — works over plain http, which is how the Pi is
 *      usually reached. Dynamically imported so browsers on tier 1 never download it.
 *   3. Neither — the caller falls back to uncompressed PCM, exactly as before.
 */

export type LiveCodec = 'opus' | 'pcm';

/** The `AUDIO_FORMAT` payload the server sends as a text frame on the audio socket. */
export interface AudioFormat {
  codec: LiveCodec;
  sampleRate: number;
  channels: number;
  frameMs?: number;
  bitrate?: number;
}

export interface StreamDecoder {
  /** Fire-and-forget. Decoded samples arrive through the callback given at construction. */
  decode(packet: ArrayBuffer): void;
  reset(): void;
  close(): void;
}

/** Planar Float32, one array per channel. Always backed by a plain (transferable) ArrayBuffer. */
export type SamplesCallback = (channels: Float32Array<ArrayBuffer>[]) => void;

const SAMPLE_RATE = 48000;
/** Every packet the server sends is one 20 ms frame. */
const FRAME_DURATION_US = 20000;

/** Parses an `AUDIO_FORMAT` text frame, returning null for anything else. */
export function parseAudioFormat(raw: string): AudioFormat | null {
  try {
    const msg = JSON.parse(raw);
    if (msg?.type !== 'AUDIO_FORMAT' || !msg.payload) return null;
    const { codec, sampleRate, channels } = msg.payload;
    if (codec !== 'opus' && codec !== 'pcm_s16le') return null;
    return {
      codec: codec === 'opus' ? 'opus' : 'pcm',
      sampleRate: typeof sampleRate === 'number' ? sampleRate : SAMPLE_RATE,
      channels: channels === 2 ? 2 : 1,
      frameMs: msg.payload.frameMs,
      bitrate: msg.payload.bitrate,
    };
  } catch {
    return null;
  }
}

let probe: Promise<LiveCodec> | null = null;

/**
 * Resolves what this browser can decode. Memoized: the answer can't change within a page load, and
 * reconnects must not re-run the wasm import.
 */
export function detectAudioCodec(): Promise<LiveCodec> {
  probe ??= runProbe();
  return probe;
}

/** Test seam — resets the memoized probe, the cached wasm module, and the load counter. */
export function resetCodecDetection(): void {
  probe = null;
  wasmModule = null;
  wasmLoads = 0;
}

/**
 * Test seam — how many times the wasm decoder module has been imported. Guards the promise that
 * browsers with native WebCodecs never download libopus.
 */
export function wasmDecoderLoadCount(): number {
  return wasmLoads;
}

async function runProbe(): Promise<LiveCodec> {
  if (await webCodecsSupportsOpus()) return 'opus';
  // No WebCodecs (older browser, or a non-secure context like http://raspberrypi.local). Only
  // claim Opus if the wasm decoder actually loads — promising it and failing later would leave
  // the user with silence.
  try {
    await loadWasmDecoderModule();
    return 'opus';
  } catch {
    return 'pcm';
  }
}

async function webCodecsSupportsOpus(): Promise<boolean> {
  if (typeof AudioDecoder === 'undefined') return false;
  try {
    const support = await AudioDecoder.isConfigSupported(opusConfig(1));
    return !!support.supported;
  } catch {
    return false;
  }
}

function opusConfig(channels: number): AudioDecoderConfig {
  // No `description`: that tells WebCodecs the input is a raw Opus packet stream rather than
  // Ogg-contained, which is exactly what the server sends.
  return { codec: 'opus', sampleRate: SAMPLE_RATE, numberOfChannels: channels };
}

type WasmDecoderModule = typeof import('opus-decoder');
let wasmModule: Promise<WasmDecoderModule> | null = null;
let wasmLoads = 0;

function loadWasmDecoderModule(): Promise<WasmDecoderModule> {
  if (!wasmModule) {
    wasmLoads++;
    wasmModule = import('opus-decoder');
  }
  return wasmModule;
}

/**
 * Builds a decoder for the given channel count, preferring WebCodecs. Returns null when neither
 * tier is usable, which tells the caller to renegotiate as PCM.
 */
export async function createOpusDecoder(
  channels: number,
  onSamples: SamplesCallback,
  onError?: (err: unknown) => void,
): Promise<StreamDecoder | null> {
  const native = await createWebCodecsDecoder(channels, onSamples, onError);
  if (native) return native;
  try {
    return await createWasmDecoder(channels, onSamples);
  } catch (err) {
    onError?.(err);
    return null;
  }
}

async function createWebCodecsDecoder(
  channels: number,
  onSamples: SamplesCallback,
  onError?: (err: unknown) => void,
): Promise<StreamDecoder | null> {
  if (typeof AudioDecoder === 'undefined') return null;

  const config = opusConfig(channels);
  try {
    if (!(await AudioDecoder.isConfigSupported(config)).supported) return null;
  } catch {
    return null;
  }

  let timestamp = 0;
  const decoder = new AudioDecoder({
    output: (data) => {
      try {
        const out: Float32Array<ArrayBuffer>[] = [];
        for (let ch = 0; ch < data.numberOfChannels; ch++) {
          const buf = new Float32Array(data.numberOfFrames);
          data.copyTo(buf, { planeIndex: ch, format: 'f32-planar' });
          out.push(buf);
        }
        onSamples(out);
      } finally {
        // Mandatory: AudioData holds a slot in the decoder's pool. Leak these and playback
        // silently stops a few hundred frames in.
        data.close();
      }
    },
    error: (err) => onError?.(err),
  });
  decoder.configure(config);

  return {
    decode(packet) {
      // Every Opus packet is independently decodable, so every chunk is a key frame. Timestamps
      // only label the output; the worklet's ring buffer owns real-time alignment.
      decoder.decode(
        new EncodedAudioChunk({ type: 'key', timestamp, duration: FRAME_DURATION_US, data: packet }),
      );
      timestamp += FRAME_DURATION_US;
    },
    reset() {
      timestamp = 0;
      try {
        decoder.reset();
        decoder.configure(config);
      } catch (err) {
        onError?.(err);
      }
    },
    close() {
      try {
        if (decoder.state !== 'closed') decoder.close();
      } catch {
        /* already gone */
      }
    },
  };
}

async function createWasmDecoder(channels: number, onSamples: SamplesCallback): Promise<StreamDecoder> {
  const { OpusDecoder } = await loadWasmDecoderModule();
  const decoder = new OpusDecoder({ channels });
  await decoder.ready;

  return {
    decode(packet) {
      const { channelData, samplesDecoded } = decoder.decodeFrame(new Uint8Array(packet));
      if (samplesDecoded <= 0) return;
      // slice() is load-bearing: opus-decoder reuses these output buffers between calls, and the
      // playback path transfers them to the worklet. Handing over the originals would neuter the
      // decoder's own memory and break every subsequent frame. It also allocates a fresh plain
      // ArrayBuffer, which is what makes the cast safe.
      onSamples(channelData.map((c) => c.slice(0, samplesDecoded) as Float32Array<ArrayBuffer>));
    },
    reset() {
      void decoder.reset();
    },
    close() {
      try {
        decoder.free();
      } catch {
        /* already freed */
      }
    },
  };
}
