/**
 * PCM ring-buffer player (AudioWorklet).
 *
 * The live scanner audio arrives as a stream of small Int16 PCM frames over a
 * WebSocket. Playing each frame as its own AudioBufferSourceNode produces clicks
 * at every frame boundary and gaps whenever a frame is late. Instead, the main
 * thread pushes Float32 samples into this processor's per-channel ring buffer,
 * and the audio thread pulls them at the hardware rate — contiguous playback,
 * and on underrun we output silence (no click) rather than a discontinuity.
 *
 * Messages from the main thread:
 *   { channels: Float32Array[] }  — append these deinterleaved samples
 *   { type: 'reset' }             — clear the buffer (e.g. context change)
 */
class PcmPlayerProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    const sr = sampleRate; // AudioWorkletGlobalScope sample rate (e.g. 48000)
    this.capacity = Math.floor(sr * 6);              // hard safety ceiling
    this.channelData = [new Float32Array(this.capacity), new Float32Array(this.capacity)];
    this.readIndex = 0;
    this.writeIndex = 0;
    this.available = 0;          // samples per channel currently buffered
    this.started = false;        // becomes true once we've primed enough
    // Target latency window. Prime this much before draining so bursty network
    // delivery can't underrun; if we drift beyond maxLatency (accumulating lag),
    // fast-forward back to targetLatency in one step (a single rare skip).
    this.primeSamples = Math.floor(sr * 0.25);
    this.targetLatency = Math.floor(sr * 0.25);
    this.maxLatency = Math.floor(sr * 0.6);

    this.port.onmessage = (e) => {
      const data = e.data;
      if (data.type === 'reset') {
        this.readIndex = this.writeIndex = this.available = 0;
        this.started = false;
        return;
      }
      const channels = data.channels;
      if (!channels || channels.length === 0) return;
      const mono = channels.length === 1;
      const left = channels[0];
      const right = mono ? channels[0] : channels[1];
      const n = left.length;
      for (let i = 0; i < n; i++) {
        if (this.available >= this.capacity) {
          this.readIndex = (this.readIndex + 1) % this.capacity;
          this.available--;
        }
        this.channelData[0][this.writeIndex] = left[i];
        this.channelData[1][this.writeIndex] = right[i];
        this.writeIndex = (this.writeIndex + 1) % this.capacity;
        this.available++;
      }
      // Bound latency: if we've buffered far more than the target, drop the
      // excess once so we don't play increasingly behind real time.
      if (this.available > this.maxLatency) {
        const drop = this.available - this.targetLatency;
        this.readIndex = (this.readIndex + drop) % this.capacity;
        this.available -= drop;
      }
    };
  }

  process(_inputs, outputs) {
    const output = outputs[0];
    const frames = output[0].length;
    const outL = output[0];
    const outR = output.length > 1 ? output[1] : null;

    if (!this.started) {
      if (this.available >= this.primeSamples) this.started = true;
      else {
        for (let i = 0; i < frames; i++) { outL[i] = 0; if (outR) outR[i] = 0; }
        return true;
      }
    }

    for (let i = 0; i < frames; i++) {
      if (this.available > 0) {
        outL[i] = this.channelData[0][this.readIndex];
        if (outR) outR[i] = this.channelData[1][this.readIndex];
        this.readIndex = (this.readIndex + 1) % this.capacity;
        this.available--;
      } else {
        // Underrun: output silence and re-prime before resuming (avoids a
        // stutter loop where we start on a single sample and immediately drain).
        outL[i] = 0;
        if (outR) outR[i] = 0;
        this.started = false;
      }
    }
    return true;
  }
}

registerProcessor('pcm-player', PcmPlayerProcessor);
