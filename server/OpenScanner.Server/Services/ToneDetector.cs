using System.Numerics;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

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

    public event Action<FireToneSet>? OnToneDetected;

    public ToneDetector(IDatabase db, ILogger<ToneDetector> logger)
    {
        _db = db;
        _logger = logger;
        ReloadTones();
    }

    public void ReloadTones()
    {
        Task.Run(async () => {
            _activeToneSets = (await _db.GetAllFireTonesAsync()).ToList();
            _logger.LogInformation($"ToneDetector: Loaded {_activeToneSets.Count} tone sets.");
        });
    }

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
        
        // Threshold check (empirical)
        return magnitude > 1.5; 
    }
}
