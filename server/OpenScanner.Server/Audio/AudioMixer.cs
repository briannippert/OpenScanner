namespace OpenScanner.Server.Audio;

/// <summary>
/// Mixes live channel audio from every SDR into one stereo stream.
///
/// <para>Each open channel has a jitter queue of mono samples. Every
/// <see cref="MixIntervalMs"/> the mixer pulls an equal-length block from each queue, pans it,
/// sums the blocks sample-aligned into one interleaved stereo buffer and emits it. Overlapping
/// transmissions play at the same time, even when different dongles hear them.</para>
///
/// <para>Fast-scanned channels are spread left to right by frequency, so each keeps a fixed
/// place in the stereo field. The layout only changes when the channel plan does. A channel
/// outside the layout (one a hopping SDR has locked on to) plays centred at full level, exactly
/// as the mono stream did before audio was mixed.</para>
/// </summary>
public sealed class AudioMixer : IDisposable
{
    /// <summary>Mix period.</summary>
    public const int MixIntervalMs = 20;

    private const int SampleRate = 48000;
    private const int MixBlockSamples = SampleRate * MixIntervalMs / 1000; // 960 samples
    private const int MaxQueueSamples = SampleRate / 2; // ~500 ms backlog cap to bound latency

    private static readonly (double Left, double Right) Centre = (1.0, 1.0);

    private readonly object _lock = new();
    private readonly Dictionary<double, Queue<short>> _queues = new();
    private Dictionary<double, (double Left, double Right)> _pans = new();
    private readonly Timer _timer;
    private bool _running;

    /// <summary>Raised with each mixed block of 48 kHz interleaved stereo.</summary>
    public event Action<AudioChunk>? OnAudio;

    /// <summary>Initializes a new instance of the <see cref="AudioMixer"/> class.</summary>
    public AudioMixer()
    {
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Sets the stereo layout: these channels are spread left to right by frequency using
    /// equal-power panning. A single channel sits in the centre.
    /// </summary>
    public void SetLayout(IEnumerable<double> frequencies)
    {
        var sorted = frequencies.Distinct().OrderBy(f => f).ToList();
        var pans = new Dictionary<double, (double, double)>();
        for (int i = 0; i < sorted.Count; i++)
        {
            double pan = sorted.Count == 1 ? 0.5 : (double)i / (sorted.Count - 1); // 0 = left, 1 = right
            pans[sorted[i]] = (Math.Cos(pan * Math.PI / 2.0), Math.Sin(pan * Math.PI / 2.0));
        }
        lock (_lock) _pans = pans;
    }

    /// <summary>The pan gains a channel would be mixed at.</summary>
    public (double Left, double Right) PanFor(double frequency)
    {
        lock (_lock) return _pans.TryGetValue(frequency, out var g) ? g : Centre;
    }

    /// <summary>Opens a channel's queue so its audio is mixed in.</summary>
    public void Open(double frequency)
    {
        lock (_lock)
        {
            _queues.TryAdd(frequency, new Queue<short>());
            if (!_running)
            {
                _running = true;
                _timer.Change(MixIntervalMs, MixIntervalMs);
            }
        }
    }

    /// <summary>Closes a channel's queue, discarding anything still buffered.</summary>
    public void Close(double frequency)
    {
        lock (_lock)
        {
            _queues.Remove(frequency);
            if (_queues.Count == 0 && _running)
            {
                _running = false;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    /// <summary>Whether a channel's queue is open.</summary>
    public bool IsOpen(double frequency)
    {
        lock (_lock) return _queues.ContainsKey(frequency);
    }

    /// <summary>
    /// Queues 16-bit little-endian mono samples for a channel. Ignored unless the channel is
    /// open. If the channel has run ahead of the mix, its oldest samples are dropped.
    /// </summary>
    public void Enqueue(double frequency, byte[] audio)
    {
        int samples = audio.Length / 2;
        lock (_lock)
        {
            if (!_queues.TryGetValue(frequency, out var queue)) return;
            for (int i = 0; i < samples; i++)
                queue.Enqueue((short)(audio[i * 2] | (audio[i * 2 + 1] << 8)));
            while (queue.Count > MaxQueueSamples)
                queue.Dequeue();
        }
    }

    /// <summary>
    /// Mixes one block. Called by the timer, and directly by tests. Channels with no buffered
    /// audio contribute silence; if none had any, nothing is emitted, so the stream only flows
    /// while something is on air.
    /// </summary>
    public void Tick()
    {
        var left = new int[MixBlockSamples];
        var right = new int[MixBlockSamples];
        bool anyAudio = false;

        lock (_lock)
        {
            foreach (var (freq, queue) in _queues)
            {
                if (queue.Count == 0) continue;
                var gains = _pans.TryGetValue(freq, out var g) ? g : Centre;

                int count = Math.Min(queue.Count, MixBlockSamples);
                for (int i = 0; i < count; i++)
                {
                    short sample = queue.Dequeue();
                    left[i] += (int)(sample * gains.Left);
                    right[i] += (int)(sample * gains.Right);
                }
                anyAudio = true;
            }
        }

        if (!anyAudio) return;

        var stereo = new byte[MixBlockSamples * 4];
        for (int i = 0; i < MixBlockSamples; i++)
        {
            short l = (short)Math.Clamp(left[i], short.MinValue, short.MaxValue);
            short r = (short)Math.Clamp(right[i], short.MinValue, short.MaxValue);
            int o = i * 4;
            stereo[o] = (byte)(l & 0xFF);
            stereo[o + 1] = (byte)((l >> 8) & 0xFF);
            stereo[o + 2] = (byte)(r & 0xFF);
            stereo[o + 3] = (byte)((r >> 8) & 0xFF);
        }

        OnAudio?.Invoke(AudioChunk.Stereo48k(stereo));
    }

    /// <inheritdoc />
    public void Dispose() => _timer.Dispose();
}
