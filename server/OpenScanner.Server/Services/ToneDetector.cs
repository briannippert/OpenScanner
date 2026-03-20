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
    
    // Detection state
    private string? _detectedToneAName;
    private DateTime _toneADetectedTime = DateTime.MinValue;
    private double _lastToneAFreq;

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

        // Check for active tones in this chunk
        // Standard FTO: Tone A (approx 1s) followed by Tone B (approx 3s)
        
        foreach (var toneSet in _activeToneSets)
        {
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
