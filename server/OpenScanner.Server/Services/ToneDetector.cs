using System.Numerics;
using OpenScanner.Server.Models;
using OpenScanner.Server.Interfaces;

namespace OpenScanner.Server.Services;

/// <summary>
/// Service for detecting Fire Tone Out (FTO) 2-tone paging sequences in audio streams.
/// </summary>
public class ToneDetector
{
    private readonly IDatabase _db;
    private readonly ILogger<ToneDetector> _logger;
    private readonly TimeProvider _time;
    private List<FireToneSet> _activeToneSets = new();
    private readonly int _sampleRate = 48000;
    
    /// <summary>
    /// Detection state for one audio stream. The detector is stateful (Tone A must be followed
    /// by Tone B; a single tone must persist), so each independent audio source needs its own
    /// copy: feeding several channels' audio into one state interleaves unrelated chunks and
    /// breaks the A-then-B timing and the sustained-tone run.
    /// </summary>
    private sealed class StreamState
    {
        // Two-tone (Quick Call II) detection state
        public string? DetectedToneAName;
        public DateTime ToneADetectedTime = DateTime.MinValue;
        public double LastToneAFreq;

        // Single (long) tone detection state, keyed by tone set name. Some agencies page
        // with a single sustained tone instead of a two-tone A/B sequence; a FireToneSet
        // with FrequencyB <= 0 is treated as single-tone.
        public readonly Dictionary<string, SingleToneRun> SingleToneRuns = new();
    }

    private readonly StreamState _defaultStream = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<double, StreamState> _streams = new();

    // A single tone must be continuously present for at least this long before it fires,
    // which rejects the brief tones that occur in speech.
    private const double SingleToneMinDurationSeconds = 1.0;

    // Momentary dropouts shorter than this don't reset a sustained-tone run.
    private const double SingleToneGapToleranceSeconds = 0.3;

    private struct SingleToneRun
    {
        public DateTime Start;
        public DateTime LastSeen;
        public bool Fired;
    }

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
    /// <param name="time">Clock used to time tone durations.</param>
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
            _activeToneSets = (await _db.GetAllFireTonesAsync()).ToList();
            _logger.LogInformation($"ToneDetector: Loaded {_activeToneSets.Count} tone sets.");
        });
    }

    /// <summary>
    /// Processes a chunk of PCM audio data to check for tone sequences.
    /// </summary>
    /// <param name="pcmData">16-bit PCM audio data.</param>
    public void ProcessAudio(byte[] pcmData) => ProcessAudio(pcmData, _defaultStream, null);

    /// <summary>
    /// Processes audio belonging to an independent stream (one parallel channel). Each key has
    /// its own detection state, and detections raise <see cref="OnStreamToneDetected"/>.
    /// </summary>
    /// <param name="pcmData">16-bit PCM audio data.</param>
    /// <param name="streamKey">Identifies the audio source, e.g. the channel frequency.</param>
    public void ProcessAudio(byte[] pcmData, double streamKey) =>
        ProcessAudio(pcmData, _streams.GetOrAdd(streamKey, _ => new StreamState()), streamKey);

    /// <summary>Drops all keyed stream state (call when parallel scanning stops).</summary>
    public void ResetStreams() => _streams.Clear();

    private void Fire(FireToneSet toneSet, double? streamKey)
    {
        if (streamKey.HasValue) OnStreamToneDetected?.Invoke(toneSet, streamKey.Value);
        else OnToneDetected?.Invoke(toneSet);
    }

    private void ProcessAudio(byte[] pcmData, StreamState st, double? streamKey)
    {
        var toneSets = _activeToneSets;
        if (toneSets.Count == 0) return;

        // Convert byte array to float samples
        int sampleCount = pcmData.Length / 2;
        float[] samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short s = BitConverter.ToInt16(pcmData, i * 2);
            samples[i] = s / 32768f;
        }

        var now = _time.GetUtcNow().UtcDateTime;

        // Check for active tones in this chunk
        // Standard FTO: Tone A (approx 1s) followed by Tone B (approx 3s)

        foreach (var toneSet in toneSets)
        {
            // A tone set with no second frequency is a single (long) tone page.
            if (toneSet.FrequencyB <= 0)
            {
                HandleSingleTone(toneSet, samples, now, st, streamKey);
                continue;
            }

            // check Tone A
            bool toneAPresent = IsFrequencyPresent(samples, toneSet.FrequencyA);
            
            if (toneAPresent)
            {
                if (st.DetectedToneAName != toneSet.Name)
                {
                    _logger.LogInformation($"ToneDetector: Potential Tone A for {toneSet.Name} ({toneSet.FrequencyA} Hz)");
                    st.DetectedToneAName = toneSet.Name;
                    st.ToneADetectedTime = now;
                    st.LastToneAFreq = toneSet.FrequencyA;
                }
            }
            
            // Check Tone B if Tone A was recently detected
            if (st.DetectedToneAName == toneSet.Name && (now - st.ToneADetectedTime).TotalSeconds < 2.0)
            {
                bool toneBPresent = IsFrequencyPresent(samples, toneSet.FrequencyB);
                if (toneBPresent && Math.Abs(toneSet.FrequencyB - st.LastToneAFreq) > 5) // ensure it's not the same tone
                {
                    _logger.LogInformation($"ToneDetector: DETECTED {toneSet.Name} (A: {toneSet.FrequencyA}, B: {toneSet.FrequencyB})");
                    Fire(toneSet, streamKey);
                    st.DetectedToneAName = null; // Reset
                }
            }
        }

        // Cleanup stale Tone A detections
        if (st.DetectedToneAName != null && (now - st.ToneADetectedTime).TotalSeconds > 3.0)
        {
            st.DetectedToneAName = null;
        }
    }

    /// <summary>
    /// Detects a single sustained tone. Fires once FrequencyA has been continuously
    /// present for <see cref="SingleToneMinDurationSeconds"/>, then stays quiet until
    /// the tone drops out so a long page only fires a single event.
    /// </summary>
    private void HandleSingleTone(FireToneSet toneSet, float[] samples, DateTime now, StreamState st, double? streamKey)
    {
        bool present = IsFrequencyPresent(samples, toneSet.FrequencyA);

        if (present)
        {
            if (!st.SingleToneRuns.TryGetValue(toneSet.Name, out var run) ||
                (now - run.LastSeen).TotalSeconds > SingleToneGapToleranceSeconds)
            {
                // No active run (or the previous one lapsed): start a fresh one.
                run = new SingleToneRun { Start = now, LastSeen = now, Fired = false };
            }
            else
            {
                run.LastSeen = now;
            }

            if (!run.Fired && (now - run.Start).TotalSeconds >= SingleToneMinDurationSeconds)
            {
                _logger.LogInformation($"ToneDetector: DETECTED single-tone {toneSet.Name} ({toneSet.FrequencyA} Hz)");
                Fire(toneSet, streamKey);
                run.Fired = true;
            }

            st.SingleToneRuns[toneSet.Name] = run;
        }
        else if (st.SingleToneRuns.TryGetValue(toneSet.Name, out var run) &&
                 (now - run.LastSeen).TotalSeconds > SingleToneGapToleranceSeconds)
        {
            // Tone has been absent long enough — end the run so it can re-fire later.
            st.SingleToneRuns.Remove(toneSet.Name);
        }
    }

    private bool IsFrequencyPresent(float[] samples, double targetFreq)
    {
        if (targetFreq <= 0) return false;

        // Goertzel Algorithm
        int n = samples.Length;
        double k = 0.5 + ((n * targetFreq) / _sampleRate);
        double omega = (2.0 * Math.PI * k) / n;
        double sine = Math.Sin(omega);
        double cosine = Math.Cos(omega);
        double coeff = 2.0 * cosine;
        double q0 = 0, q1 = 0, q2 = 0;

        foreach (float sample in samples)
        {
            q0 = coeff * q1 - q2 + sample;
            q2 = q1;
            q1 = q0;
        }

        double magnitude = Math.Sqrt(q1 * q1 + q2 * q2 - q1 * q2 * coeff);
        
        // Threshold check (relative to signal strength/length)
        // Magnitude is roughly proportional to N * Amplitude / 2
        // We want a robust threshold.
        double threshold = samples.Length * 0.1; // Require avg amplitude > 0.2 approx?
        
        // _logger.LogInformation($"Freq {targetFreq}: Mag {magnitude:F1} (Thresh {threshold:F1})");

        return magnitude > threshold; 
    }
}
