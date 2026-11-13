using System.Numerics;
using OpenScanner.Server.Models;
using OpenScanner.Server.Interfaces;

namespace OpenScanner.Server.Services;

/// <summary>
/// Detects Fire Tone Out pages in audio: two-tone sequential pages (Quick Call II: tone A,
/// typically ~1 s, then tone B, typically ~3 s) and single long-tone pages.
///
/// Audio is analysed in fixed 100 ms Hann-windowed blocks with 50 ms hops, whatever chunk
/// size the caller delivers. A tone counts as present in a block when it carries at least
/// <see cref="MinPurity"/> of the block's energy, so detection depends on how pure the
/// signal is, not how loud. Each tone must also be held for a minimum time
/// (<see cref="MinToneASeconds"/>, <see cref="MinToneBSeconds"/>). Speech often touches a
/// tone frequency for a moment but never holds one, so a dispatcher's voice does not
/// trigger pages.
/// </summary>
public class ToneDetector
{
    private readonly IDatabase _db;
    private readonly ILogger<ToneDetector> _logger;
    private readonly TimeProvider _time;
    private List<FireToneSet> _activeToneSets = new();
    // Distinct frequencies across the active tone sets, analysed once per block.
    private double[] _frequencies = Array.Empty<double>();

    private const int SampleRate = 48000;

    /// <summary>Analysis block length: 100 ms, so ~10 Hz frequency resolution.</summary>
    internal const int BlockSamples = SampleRate / 10;

    /// <summary>Hop between blocks: 50 ms.</summary>
    internal const int HopSamples = BlockSamples / 2;

    /// <summary>Share of a block's energy a tone must carry to count as present.</summary>
    internal const double MinPurity = 0.5;

    /// <summary>Blocks quieter than this RMS (about -46 dBFS) are treated as silence.</summary>
    internal const double MinRms = 0.005;

    /// <summary>Minimum length of tone A in a two-tone page.</summary>
    internal const double MinToneASeconds = 0.5;

    /// <summary>
    /// Minimum length of tone B in a two-tone page. Tone B is normally ~3 s, while speech
    /// was measured holding a tone frequency for up to ~0.45 s, so this leaves headroom.
    /// </summary>
    internal const double MinToneBSeconds = 0.8;

    /// <summary>Longest gap allowed between the end of tone A and the start of tone B.</summary>
    internal const double MaxToneGapSeconds = 1.0;

    /// <summary>A single-tone page must be held at least this long.</summary>
    internal const double SingleToneMinDurationSeconds = 1.0;

    /// <summary>Dropouts up to this long don't break a tone (fades, a lost block).</summary>
    internal const double DropoutToleranceSeconds = 0.15;

    /// <summary>
    /// When a stream receives no audio for this long (squelch closed, channel idle), its
    /// state is dropped, so tone A from one transmission never pairs with tone B from a
    /// later one.
    /// </summary>
    internal static readonly TimeSpan StreamIdleReset = TimeSpan.FromSeconds(1);

    private static readonly double[] Window = BuildHann(BlockSamples);
    private static readonly double WindowSum = Window.Sum();
    private static readonly double WindowSumSquares = Window.Sum(w => w * w);

    /// <summary>A continuous stretch of one tone, in samples on the stream's own clock.</summary>
    private sealed class ToneRun
    {
        public long Id;
        public long Start;
        public long End;
        public double Seconds => (End - Start) / (double)SampleRate;
    }

    /// <summary>Per-tone-set progress through a page.</summary>
    private sealed class PageState
    {
        // The qualifying tone-A run (two-tone), or 0.
        public long ToneARunId;
        public long ToneAStart;
        public long ToneAEnd;
        // The run that already fired, so one page fires once.
        public long FiredRunId;
    }

    /// <summary>
    /// Detection state for one audio stream. The detector is stateful (tone A must be
    /// followed by tone B; a single tone must persist), so each independent audio source
    /// needs its own copy: feeding several channels' audio into one state interleaves
    /// unrelated chunks and breaks the timing.
    /// </summary>
    private sealed class StreamState
    {
        public float[] Buffer = new float[BlockSamples * 2];
        public int Buffered;
        // Stream clock: the sample index of Buffer[0].
        public long BufferStart;
        public DateTime LastChunk = DateTime.MinValue;
        public long NextRunId = 1;
        public readonly Dictionary<double, ToneRun> Runs = new();
        public readonly Dictionary<string, PageState> Pages = new();

        public void Reset()
        {
            Buffered = 0;
            BufferStart = 0;
            Runs.Clear();
            Pages.Clear();
        }
    }

    private readonly StreamState _defaultStream = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<double, StreamState> _streams = new();

    /// <summary>
    /// Event triggered when a complete 2-tone sequence is detected.
    /// </summary>
    public event Action<FireToneSet>? OnToneDetected;

    /// <summary>
    /// Event triggered when a tone is detected in a keyed stream (see
    /// <see cref="ProcessAudio(byte[], double)"/>). The key identifies which stream — in
    /// parallel scanning, the channel frequency — heard the tone.
    /// </summary>
    public event Action<FireToneSet, double>? OnStreamToneDetected;

    /// <summary>
    /// Number of tone sets currently loaded. Useful for test synchronization after ReloadTones.
    /// </summary>
    public int ToneCount => _activeToneSets.Count;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToneDetector"/> class.
    /// </summary>
    /// <param name="db">Database interface.</param>
    /// <param name="logger">Logger instance.</param>
    public ToneDetector(IDatabase db, ILogger<ToneDetector> logger)
        : this(db, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ToneDetector"/> class with an explicit clock.
    /// </summary>
    /// <param name="db">Database interface.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="time">Clock used to notice when a stream goes idle.</param>
    public ToneDetector(IDatabase db, ILogger<ToneDetector> logger, TimeProvider time)
    {
        _db = db;
        _logger = logger;
        _time = time;
        ReloadTones();
    }

    /// <summary>
    /// Reloads the list of monitored tone sets from the database.
    /// </summary>
    public void ReloadTones()
    {
        Task.Run(async () => {
            var sets = (await _db.GetAllFireTonesAsync()).ToList();
            _frequencies = sets
                .SelectMany(t => new[] { t.FrequencyA, t.FrequencyB })
                .Where(f => f > 0 && f < SampleRate / 2.0)
                .Distinct()
                .ToArray();
            _activeToneSets = sets;
            _logger.LogInformation($"ToneDetector: Loaded {sets.Count} tone sets.");
        });
    }

    /// <summary>
    /// Processes a chunk of PCM audio data to check for tone sequences.
    /// </summary>
    /// <param name="pcmData">16-bit PCM audio data at 48 kHz.</param>
    public void ProcessAudio(byte[] pcmData) => ProcessAudio(pcmData, _defaultStream, null);

    /// <summary>
    /// Processes audio belonging to an independent stream (one parallel channel). Each key has
    /// its own detection state, and detections raise <see cref="OnStreamToneDetected"/>.
    /// </summary>
    /// <param name="pcmData">16-bit PCM audio data at 48 kHz.</param>
    /// <param name="streamKey">Identifies the audio source, e.g. the channel frequency.</param>
    public void ProcessAudio(byte[] pcmData, double streamKey) =>
        ProcessAudio(pcmData, _streams.GetOrAdd(streamKey, _ => new StreamState()), streamKey);

    /// <summary>Drops all keyed stream state (call when parallel scanning stops).</summary>
    public void ResetStreams() => _streams.Clear();

    /// <summary>
    /// Drops one keyed stream's state. With several SDRs each resets only its own channels;
    /// <see cref="ResetStreams"/> would wipe tone detection in progress on the others.
    /// </summary>
    public void ResetStream(double streamKey) => _streams.TryRemove(streamKey, out _);

    private void Fire(FireToneSet toneSet, double? streamKey)
    {
        if (streamKey.HasValue) OnStreamToneDetected?.Invoke(toneSet, streamKey.Value);
        else OnToneDetected?.Invoke(toneSet);
    }

    private void ProcessAudio(byte[] pcmData, StreamState st, double? streamKey)
    {
        var toneSets = _activeToneSets;
        var frequencies = _frequencies;
        if (toneSets.Count == 0 || frequencies.Length == 0) return;

        var now = _time.GetUtcNow().UtcDateTime;
        if (st.LastChunk != DateTime.MinValue && now - st.LastChunk > StreamIdleReset) st.Reset();
        st.LastChunk = now;

        int sampleCount = pcmData.Length / 2;
        if (st.Buffered + sampleCount > st.Buffer.Length)
        {
            Array.Resize(ref st.Buffer, Math.Max(st.Buffer.Length * 2, st.Buffered + sampleCount));
        }
        for (int i = 0; i < sampleCount; i++)
        {
            st.Buffer[st.Buffered + i] = BitConverter.ToInt16(pcmData, i * 2) / 32768f;
        }
        st.Buffered += sampleCount;

        int offset = 0;
        while (st.Buffered - offset >= BlockSamples)
        {
            AnalyzeBlock(st, st.Buffer.AsSpan(offset, BlockSamples), st.BufferStart + offset, frequencies, toneSets, streamKey);
            offset += HopSamples;
        }
        if (offset > 0)
        {
            Array.Copy(st.Buffer, offset, st.Buffer, 0, st.Buffered - offset);
            st.Buffered -= offset;
            st.BufferStart += offset;
        }
    }

    private void AnalyzeBlock(StreamState st, ReadOnlySpan<float> block, long blockStart, double[] frequencies,
        List<FireToneSet> toneSets, double? streamKey)
    {
        long blockEnd = blockStart + BlockSamples;
        long tolerance = (long)(DropoutToleranceSeconds * SampleRate);

        // Windowed energy, as the mean power of the windowed signal.
        double energy = 0;
        for (int i = 0; i < block.Length; i++)
        {
            double v = block[i] * Window[i];
            energy += v * v;
        }
        double meanPower = energy / WindowSumSquares;
        bool loudEnough = meanPower >= MinRms * MinRms;

        foreach (var freq in frequencies)
        {
            if (!loudEnough || Purity(block, freq, meanPower) < MinPurity) continue;

            // Present: extend the current run, or start a new one after a real gap.
            if (st.Runs.TryGetValue(freq, out var run) && blockStart - run.End <= tolerance)
            {
                run.End = blockEnd;
            }
            else
            {
                st.Runs[freq] = new ToneRun { Id = st.NextRunId++, Start = blockStart, End = blockEnd };
            }
        }

        foreach (var set in toneSets)
        {
            if (!st.Pages.TryGetValue(set.Name, out var page))
            {
                page = new PageState();
                st.Pages[set.Name] = page;
            }
            if (set.FrequencyB <= 0) CheckSingleTone(set, st, page, blockEnd, streamKey);
            else CheckTwoTone(set, st, page, blockEnd, streamKey);
        }
    }

    private void CheckSingleTone(FireToneSet set, StreamState st, PageState page, long blockEnd, double? streamKey)
    {
        if (!st.Runs.TryGetValue(set.FrequencyA, out var run) || run.End != blockEnd) return;
        if (run.Id == page.FiredRunId || run.Seconds < SingleToneMinDurationSeconds) return;

        page.FiredRunId = run.Id;
        _logger.LogInformation("ToneDetector: DETECTED single-tone {Name} ({Freq} Hz)", set.Name, set.FrequencyA);
        Fire(set, streamKey);
    }

    private void CheckTwoTone(FireToneSet set, StreamState st, PageState page, long blockEnd, double? streamKey)
    {
        // Remember the latest tone A that was held long enough.
        if (st.Runs.TryGetValue(set.FrequencyA, out var a) && a.Seconds >= MinToneASeconds)
        {
            if (a.Id != page.ToneARunId)
            {
                _logger.LogDebug("ToneDetector: tone A for {Name} ({Freq} Hz)", set.Name, set.FrequencyA);
            }
            page.ToneARunId = a.Id;
            page.ToneAStart = a.Start;
            page.ToneAEnd = a.End;
        }

        if (page.ToneARunId == 0) return;
        if (!st.Runs.TryGetValue(set.FrequencyB, out var b) || b.End != blockEnd) return;
        if (b.Id == page.FiredRunId || b.Seconds < MinToneBSeconds) return;

        // Tone B must follow tone A: start after it began, and within the allowed gap of its end.
        // The overlapping blocks at the A/B boundary may see both, so B may start slightly
        // before A's last block ends.
        if (b.Start <= page.ToneAStart) return;
        if ((b.Start - page.ToneAEnd) / (double)SampleRate > MaxToneGapSeconds) return;

        page.FiredRunId = b.Id;
        page.ToneARunId = 0;
        _logger.LogInformation("ToneDetector: DETECTED {Name} (A: {A}, B: {B})", set.Name, set.FrequencyA, set.FrequencyB);
        Fire(set, streamKey);
    }

    /// <summary>
    /// The share of the block's power at <paramref name="freq"/>: 1 for a pure tone at that
    /// frequency, near 0 when the block is something else. Goertzel on the Hann-windowed
    /// block, normalised by the window so it doesn't depend on block length or level.
    /// </summary>
    private static double Purity(ReadOnlySpan<float> block, double freq, double meanPower)
    {
        if (meanPower <= 0) return 0;
        double omega = 2.0 * Math.PI * freq / SampleRate;
        double coeff = 2.0 * Math.Cos(omega);
        double q1 = 0, q2 = 0;
        for (int i = 0; i < block.Length; i++)
        {
            double q0 = coeff * q1 - q2 + block[i] * Window[i];
            q2 = q1;
            q1 = q0;
        }
        double magSquared = q1 * q1 + q2 * q2 - q1 * q2 * coeff;
        // A sine of amplitude A gives |X| = A * sum(w) / 2, i.e. power A^2/2.
        double tonePower = 2.0 * magSquared / (WindowSum * WindowSum);
        return tonePower / meanPower;
    }

    private static double[] BuildHann(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / n);
        return w;
    }
}
