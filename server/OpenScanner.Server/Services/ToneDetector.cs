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
    private List<FireToneSet> _activeToneSets = new();
    private readonly int _sampleRate = 48000;
    
    // Two-tone (Quick Call II) detection state
    private string? _detectedToneAName;
    private DateTime _toneADetectedTime = DateTime.MinValue;
    private double _lastToneAFreq;

    // Single (long) tone detection state, keyed by tone set name. Some agencies page
    // with a single sustained tone instead of a two-tone A/B sequence; a FireToneSet
    // with FrequencyB <= 0 is treated as single-tone.
    private readonly Dictionary<string, SingleToneRun> _singleToneRuns = new();

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
    /// Number of tone sets currently loaded. Useful for test synchronization after ReloadTones.
    /// </summary>
    public int ToneCount => _activeToneSets.Count;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToneDetector"/> class.
    /// </summary>
    /// <param name="db">Database interface.</param>
    /// <param name="logger">Logger instance.</param>
    public ToneDetector(IDatabase db, ILogger<ToneDetector> logger)
    {
        _db = db;
        _logger = logger;
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
    public void ProcessAudio(byte[] pcmData)
    {
        if (_activeToneSets.Count == 0) return;

        // Convert byte array to float samples
        int sampleCount = pcmData.Length / 2;
        float[] samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short s = BitConverter.ToInt16(pcmData, i * 2);
            samples[i] = s / 32768f;
        }

        var now = DateTime.UtcNow;

        // Check for active tones in this chunk
        // Standard FTO: Tone A (approx 1s) followed by Tone B (approx 3s)

        foreach (var toneSet in _activeToneSets)
        {
            // A tone set with no second frequency is a single (long) tone page.
            if (toneSet.FrequencyB <= 0)
            {
                HandleSingleTone(toneSet, samples, now);
                continue;
            }

            // check Tone A
            bool toneAPresent = IsFrequencyPresent(samples, toneSet.FrequencyA);
            
            if (toneAPresent)
            {
                if (_detectedToneAName != toneSet.Name)
                {
                    _logger.LogInformation($"ToneDetector: Potential Tone A for {toneSet.Name} ({toneSet.FrequencyA} Hz)");
                    _detectedToneAName = toneSet.Name;
                    _toneADetectedTime = DateTime.UtcNow;
                    _lastToneAFreq = toneSet.FrequencyA;
                }
            }
            
            // Check Tone B if Tone A was recently detected
            if (_detectedToneAName == toneSet.Name && (DateTime.UtcNow - _toneADetectedTime).TotalSeconds < 2.0)
            {
                bool toneBPresent = IsFrequencyPresent(samples, toneSet.FrequencyB);
                if (toneBPresent && Math.Abs(toneSet.FrequencyB - _lastToneAFreq) > 5) // ensure it's not the same tone
                {
                    _logger.LogInformation($"ToneDetector: DETECTED {toneSet.Name} (A: {toneSet.FrequencyA}, B: {toneSet.FrequencyB})");
                    OnToneDetected?.Invoke(toneSet);
                    _detectedToneAName = null; // Reset
                }
            }
        }

        // Cleanup stale Tone A detections
        if (_detectedToneAName != null && (DateTime.UtcNow - _toneADetectedTime).TotalSeconds > 3.0)
        {
            _detectedToneAName = null;
        }
    }

    /// <summary>
    /// Detects a single sustained tone. Fires once FrequencyA has been continuously
    /// present for <see cref="SingleToneMinDurationSeconds"/>, then stays quiet until
    /// the tone drops out so a long page only fires a single event.
    /// </summary>
    private void HandleSingleTone(FireToneSet toneSet, float[] samples, DateTime now)
    {
        bool present = IsFrequencyPresent(samples, toneSet.FrequencyA);

        if (present)
        {
            if (!_singleToneRuns.TryGetValue(toneSet.Name, out var run) ||
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
                OnToneDetected?.Invoke(toneSet);
                run.Fired = true;
            }

            _singleToneRuns[toneSet.Name] = run;
        }
        else if (_singleToneRuns.TryGetValue(toneSet.Name, out var run) &&
                 (now - run.LastSeen).TotalSeconds > SingleToneGapToleranceSeconds)
        {
            // Tone has been absent long enough — end the run so it can re-fire later.
            _singleToneRuns.Remove(toneSet.Name);
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
