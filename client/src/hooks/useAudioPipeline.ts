import { useCallback, useEffect, useRef, useState } from 'react';

export interface NowPlaying {
  id: string;
  filename: string;
  label?: string;
  duration: number;
  /** Normalized waveform peaks (0..1) for the scrubber. */
  peaks: number[];
}

/** Downsample an AudioBuffer to `buckets` normalized peak amplitudes. */
function computePeaks(buffer: AudioBuffer, buckets = 400): number[] {
  const data = buffer.getChannelData(0);
  const block = Math.max(1, Math.floor(data.length / buckets));
  const peaks: number[] = [];
  let max = 0;
  for (let i = 0; i < buckets; i++) {
    let peak = 0;
    const start = i * block;
    for (let j = 0; j < block && start + j < data.length; j++) {
      const v = Math.abs(data[start + j]);
      if (v > peak) peak = v;
    }
    peaks.push(peak);
    if (peak > max) max = peak;
  }
  return max > 0 ? peaks.map(p => p / max) : peaks;
}

/**
 * Owns the Web Audio pipeline: the shared AudioContext, gain/filter/analyser
 * graph, live PCM streaming over the audio WebSocket, and recorded-clip
 * playback with a seekable transport (position, pause, rate) for the Now Playing bar.
 *
 * @param volume 0..1, driven by the header slider. Gain is smoothed toward it.
 */
export function useAudioPipeline(volume: number) {
  const [isAudioInitialized, setIsAudioInitialized] = useState(false);
  const [audioAnalyser, setAudioAnalyser] = useState<AnalyserNode | undefined>(undefined);
  const [nowPlaying, setNowPlaying] = useState<NowPlaying | null>(null);
  const [isPaused, setIsPaused] = useState(false);
  const [positionSec, setPositionSec] = useState(0);
  const [playbackRate, setPlaybackRateState] = useState(1);

  const wsAudio = useRef<WebSocket | null>(null);
  const audioAnalyserRef = useRef<AnalyserNode | null>(null);
  const gainNodeRef = useRef<GainNode | null>(null);
  const filterNodeRef = useRef<BiquadFilterNode | null>(null);
  // Live-stream ring-buffer player (AudioWorklet) — replaces per-frame source
  // scheduling where supported. AudioWorklet requires a secure context, so on the
  // Pi over plain http:// it's unavailable and we fall back to scheduled buffer
  // sources (see nextStartTime below).
  const workletNodeRef = useRef<AudioWorkletNode | null>(null);
  const workletCtxRef = useRef<BaseAudioContext | null>(null);
  const workletSetupRef = useRef<Promise<AudioWorkletNode | null> | null>(null);
  const nextStartTime = useRef<number>(0); // fallback scheduler cursor
  const isPageHiddenRef = useRef(false);
  const isParallelRef = useRef(false);
  const volumeRef = useRef(volume);

  // Playback transport state.
  const sourceRef = useRef<AudioBufferSourceNode | null>(null);
  const bufferRef = useRef<AudioBuffer | null>(null);
  const startedAtRef = useRef(0);   // ctx.currentTime when the current source started
  const offsetRef = useRef(0);      // buffer offset (sec) the current source started from
  const rateRef = useRef(1);
  const rafRef = useRef<number | null>(null);
  const nowPlayingRef = useRef<NowPlaying | null>(null);
  const durationRef = useRef(0);

  useEffect(() => {
    volumeRef.current = volume;
    if (gainNodeRef.current) {
      gainNodeRef.current.gain.setTargetAtTime(volume, window.audioCtx?.currentTime || 0, 0.05);
    }
    localStorage.setItem('scannerVolume', volume.toString());
  }, [volume]);

  useEffect(() => { nowPlayingRef.current = nowPlaying; }, [nowPlaying]);

  const setParallel = useCallback((parallel: boolean) => {
    isParallelRef.current = parallel;
  }, []);

  const ensureCtx = useCallback(async (): Promise<AudioContext | null> => {
    let ctx = window.audioCtx;
    if (!ctx) {
      const AudioContextClass = window.AudioContext || window.webkitAudioContext;
      if (!AudioContextClass) return null;
      ctx = new AudioContextClass({ sampleRate: 48000 });
      window.audioCtx = ctx;
    }
    if (ctx.state === 'suspended') await ctx.resume();
    if (!gainNodeRef.current || gainNodeRef.current.context !== ctx) {
      const gainNode = ctx.createGain();
      gainNode.gain.value = volumeRef.current;
      gainNode.connect(ctx.destination);
      gainNodeRef.current = gainNode;
    }
    return ctx;
  }, []);

  // Build (once per context) the live-stream graph: [worklet] → analyser → filter
  // → gain. Returns the worklet node, or null when AudioWorklet is unavailable
  // (non-secure context, e.g. the Pi over http://) — callers then fall back to
  // scheduling buffer sources into the same analyser.
  const ensureLiveGraph = useCallback(async (ctx: AudioContext): Promise<AudioWorkletNode | null> => {
    if (audioAnalyserRef.current && workletCtxRef.current === ctx) return workletNodeRef.current;
    if (!workletSetupRef.current || workletCtxRef.current !== ctx) {
      workletCtxRef.current = ctx;
      workletSetupRef.current = (async () => {
        if (!gainNodeRef.current || gainNodeRef.current.context !== ctx) {
          const gainNode = ctx.createGain();
          gainNode.gain.value = volumeRef.current;
          gainNode.connect(ctx.destination);
          gainNodeRef.current = gainNode;
        }
        const filter = ctx.createBiquadFilter();
        filter.type = 'lowpass';
        filter.frequency.value = 3400; // voice band; less muffled than the old 2 kHz
        filter.connect(gainNodeRef.current);
        filterNodeRef.current = filter;

        const analyser = ctx.createAnalyser();
        analyser.fftSize = 1024;
        analyser.connect(filter);
        audioAnalyserRef.current = analyser;
        setAudioAnalyser(analyser);

        // AudioWorklet is only available in a secure context. When it isn't, leave
        // the node null and let the caller use the buffer-source fallback.
        let node: AudioWorkletNode | null = null;
        if (ctx.audioWorklet) {
          try {
            await ctx.audioWorklet.addModule('/pcm-player-processor.js');
            node = new AudioWorkletNode(ctx, 'pcm-player', { outputChannelCount: [2] });
            node.connect(analyser);
          } catch (err) {
            if (!(err instanceof Error) || !/already|registered/i.test(err.message)) {
              console.error('[Audio] Worklet unavailable, using buffer-source fallback:', err);
            }
            node = null;
          }
        }
        workletNodeRef.current = node;
        return node;
      })();
    }
    return workletSetupRef.current;
  }, []);

  const initAudio = useCallback(async () => {
    const ctx = await ensureCtx();
    if (ctx) await ensureLiveGraph(ctx);
    setIsAudioInitialized(true);
  }, [ensureCtx, ensureLiveGraph]);

  useEffect(() => {
    window.addEventListener('click', initAudio);
    window.addEventListener('touchstart', initAudio);
    return () => {
      window.removeEventListener('click', initAudio);
      window.removeEventListener('touchstart', initAudio);
    };
  }, [initAudio]);

  useEffect(() => {
    const onVisibilityChange = () => {
      isPageHiddenRef.current = document.visibilityState === 'hidden';
      // Clear any stale buffered audio so we resume live, not behind.
      if (document.visibilityState === 'visible') {
        workletNodeRef.current?.port.postMessage({ type: 'reset' });
      }
    };
    document.addEventListener('visibilitychange', onVisibilityChange);
    return () => document.removeEventListener('visibilitychange', onVisibilityChange);
  }, []);

  const stopRaf = useCallback(() => {
    if (rafRef.current != null) {
      cancelAnimationFrame(rafRef.current);
      rafRef.current = null;
    }
  }, []);

  const currentPosition = useCallback((ctx: AudioContext) =>
    offsetRef.current + (ctx.currentTime - startedAtRef.current) * rateRef.current, []);

  // Stop the current source. Its onended uses an identity check, so replacing
  // sourceRef (here → null, or in startFrom → the new source) makes any stale
  // onended a no-op — this is what makes rapid scrubbing safe.
  const stopSource = useCallback(() => {
    if (sourceRef.current) {
      const s = sourceRef.current;
      sourceRef.current = null;
      try { s.stop(); } catch { /* already stopped */ }
    }
  }, []);

  const stop = useCallback(() => {
    stopSource();
    stopRaf();
    bufferRef.current = null;
    setNowPlaying(null);
    setIsPaused(false);
    setPositionSec(0);
  }, [stopSource, stopRaf]);

  // Start a source playing from `offsetSec`, wiring the position ticker.
  const startFrom = useCallback((offsetSec: number) => {
    const ctx = window.audioCtx;
    const buffer = bufferRef.current;
    if (!ctx || !buffer || !gainNodeRef.current) return;
    stopSource();
    const source = ctx.createBufferSource();
    source.buffer = buffer;
    source.playbackRate.value = rateRef.current;
    source.connect(gainNodeRef.current);
    source.onended = () => {
      // Only the source that is still current represents a natural end-of-clip;
      // a source we replaced (seek/pause/stop) is no longer current → ignore.
      if (sourceRef.current !== source) return;
      stopRaf();
      sourceRef.current = null;
      bufferRef.current = null;
      setIsPaused(false);
      setPositionSec(durationRef.current);
      setNowPlaying(null);
    };
    source.start(0, Math.max(0, offsetSec));
    sourceRef.current = source;
    startedAtRef.current = ctx.currentTime;
    offsetRef.current = offsetSec;
    setIsPaused(false);

    stopRaf();
    const tick = () => {
      const c = window.audioCtx;
      if (!c) return;
      const pos = Math.min(durationRef.current, currentPosition(c));
      setPositionSec(pos);
      rafRef.current = requestAnimationFrame(tick);
    };
    rafRef.current = requestAnimationFrame(tick);
  }, [stopSource, stopRaf, currentPosition]);

  /** Play (or toggle off) a recorded clip. */
  const playRawAudio = useCallback(async (id: string, filename: string, duration?: number, label?: string) => {
    const ctx = await ensureCtx();
    if (!ctx) return;

    // Toggle: clicking the currently-loaded clip stops it.
    if (nowPlayingRef.current?.id === id) {
      stop();
      return;
    }
    stop();

    try {
      const response = await fetch(`/audio/${filename}`);
      if (!response.ok) {
        console.error(`Failed to load audio: ${response.status} ${response.statusText}`);
        return;
      }
      const arrayBuffer = await response.arrayBuffer();
      let buffer: AudioBuffer;

      if (filename.endsWith('.raw')) {
        const int16Array = new Int16Array(arrayBuffer);
        const float32Array = new Float32Array(int16Array.length);
        for (let i = 0; i < int16Array.length; i++) float32Array[i] = int16Array[i] / 32768;
        let sampleRate = 48000;
        if (duration && duration > 0) {
          const calculatedRate = int16Array.length / duration;
          if (Math.abs(calculatedRate - 8000) < Math.abs(calculatedRate - 48000)) sampleRate = 8000;
        }
        buffer = ctx.createBuffer(1, float32Array.length, sampleRate);
        buffer.copyToChannel(float32Array, 0);
      } else {
        buffer = await ctx.decodeAudioData(arrayBuffer);
      }

      bufferRef.current = buffer;
      durationRef.current = buffer.duration;
      rateRef.current = playbackRate;
      setNowPlaying({ id, filename, label, duration: buffer.duration, peaks: computePeaks(buffer) });
      setPositionSec(0);
      startFrom(0);
    } catch (e) {
      console.error('Playback failed:', e);
      stop();
    }
  }, [ensureCtx, stop, startFrom, playbackRate]);

  const togglePause = useCallback(() => {
    const ctx = window.audioCtx;
    if (!ctx || !bufferRef.current) return;
    if (isPaused) {
      startFrom(offsetRef.current);
    } else {
      const pos = Math.min(durationRef.current, currentPosition(ctx));
      stopSource();
      stopRaf();
      offsetRef.current = pos;
      setPositionSec(pos);
      setIsPaused(true);
    }
  }, [isPaused, startFrom, stopSource, stopRaf, currentPosition]);

  const seek = useCallback((sec: number) => {
    if (!bufferRef.current) return;
    const target = Math.max(0, Math.min(durationRef.current, sec));
    setPositionSec(target);
    if (isPaused) {
      offsetRef.current = target;
    } else {
      startFrom(target);
    }
  }, [isPaused, startFrom]);

  const setRate = useCallback((rate: number) => {
    setPlaybackRateState(rate);
    const ctx = window.audioCtx;
    if (ctx && sourceRef.current && !isPaused) {
      // Rebase bookkeeping so position stays continuous, then change speed live.
      const pos = Math.min(durationRef.current, currentPosition(ctx));
      offsetRef.current = pos;
      startedAtRef.current = ctx.currentTime;
      rateRef.current = rate;
      sourceRef.current.playbackRate.value = rate;
    } else {
      rateRef.current = rate;
    }
  }, [isPaused, currentPosition]);

  // Live audio WebSocket: streams raw Int16 PCM frames, scheduled to avoid gaps.
  useEffect(() => {
    const wsProtocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsAudioUrl = `${wsProtocol}//${window.location.host}/ws/audio`;
    let closed = false;

    const connectAudioWs = () => {
      wsAudio.current = new WebSocket(wsAudioUrl);
      wsAudio.current.onclose = () => { if (!closed) setTimeout(connectAudioWs, 3000); };
      wsAudio.current.onmessage = async (event) => {
        if (!(event.data instanceof Blob)) return;
        if (isPageHiddenRef.current) return;

        try {
          const ctx = await ensureCtx();
          if (!ctx) return;
          const node = await ensureLiveGraph(ctx);
          const analyser = audioAnalyserRef.current;
          if (!analyser) return;

          const arrayBuffer = await event.data.arrayBuffer();
          const int16Array = new Int16Array(arrayBuffer);
          const isStereo = isParallelRef.current;

          // Deinterleave Int16 → Float32.
          let left: Float32Array<ArrayBuffer>;
          let right: Float32Array<ArrayBuffer>;
          if (isStereo && int16Array.length >= 2) {
            const frameSamples = Math.floor(int16Array.length / 2);
            left = new Float32Array(frameSamples);
            right = new Float32Array(frameSamples);
            for (let i = 0; i < frameSamples; i++) {
              left[i] = int16Array[i * 2] / 32768;
              right[i] = int16Array[i * 2 + 1] / 32768;
            }
          } else {
            left = new Float32Array(int16Array.length);
            for (let i = 0; i < int16Array.length; i++) left[i] = int16Array[i] / 32768;
            right = left;
          }

          if (node) {
            // Preferred path: hand off to the ring-buffer worklet (contiguous
            // playback, no per-frame boundary pops), transferring the buffers.
            if (left === right) {
              node.port.postMessage({ channels: [left] }, [left.buffer]);
            } else {
              node.port.postMessage({ channels: [left, right] }, [left.buffer, right.buffer]);
            }
          } else {
            // Fallback (no AudioWorklet, e.g. http://): schedule a buffer source
            // into the analyser with a jitter buffer.
            const frames = left.length;
            const stereo = left !== right;
            const audioBuffer = ctx.createBuffer(stereo ? 2 : 1, frames, 48000);
            audioBuffer.copyToChannel(left, 0);
            if (stereo) audioBuffer.copyToChannel(right, 1);

            const source = ctx.createBufferSource();
            source.buffer = audioBuffer;
            source.connect(analyser);

            const currentTime = ctx.currentTime;
            const JITTER_BUFFER = 0.2;
            const MAX_DRIFT = 0.6;
            if (nextStartTime.current < currentTime || nextStartTime.current > currentTime + MAX_DRIFT) {
              nextStartTime.current = currentTime + JITTER_BUFFER;
            }
            source.start(nextStartTime.current);
            nextStartTime.current += audioBuffer.duration;
          }
        } catch (err) {
          console.error('[Audio] Processing error:', err);
        }
      };
    };

    const resumeAudio = () => {
      if (window.audioCtx && window.audioCtx.state === 'suspended') window.audioCtx.resume();
    };
    window.addEventListener('click', resumeAudio);
    connectAudioWs();

    return () => {
      closed = true;
      window.removeEventListener('click', resumeAudio);
      wsAudio.current?.close();
    };
    // ensureCtx/ensureLiveGraph are stable (memoized with empty deps); listed to
    // satisfy exhaustive-deps without re-running the socket setup.
  }, [ensureCtx, ensureLiveGraph]);

  return {
    isAudioInitialized,
    audioAnalyser,
    playingId: nowPlaying?.id ?? null,
    nowPlaying,
    isPaused,
    positionSec,
    playbackRate,
    initAudio,
    playRawAudio,
    togglePause,
    seek,
    setRate,
    stop,
    setParallel,
  };
}
