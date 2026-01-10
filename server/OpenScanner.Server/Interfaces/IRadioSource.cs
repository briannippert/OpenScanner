using OpenScanner.Server.Models;

namespace OpenScanner.Server.Interfaces;

/// <summary>
/// Interface for a radio source (hardware or mock).
/// </summary>
public interface IRadioSource
{
    /// <summary>
    /// Event triggered when the scanner state changes.
    /// </summary>
    event Action<ScannerState>? OnStateChanged;

    /// <summary>
    /// Event triggered when a new call log is created.
    /// </summary>
    event Action<CallLog>? OnNewLog;

    /// <summary>
    /// Event triggered when new audio data is available.
    /// </summary>
    event Action<byte[]>? OnAudio;

    /// <summary>
    /// Gets the current state of the scanner.
    /// </summary>
    /// <returns>The current <see cref="ScannerState"/>.</returns>
    ScannerState GetState();

    /// <summary>
    /// Reloads the channel list from the database.
    /// </summary>
    void ReloadChannels();

    /// <summary>
    /// Sets the squelch threshold.
    /// </summary>
    /// <param name="db">The threshold in dB.</param>
    void SetSquelch(double db);

    /// <summary>
    /// Starts the radio source scanning process.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops the radio source.
    /// </summary>
    void Stop();

    /// <summary>
    /// Holds the scanner on a specific frequency.
    /// </summary>
    /// <param name="freq">The frequency in MHz.</param>
    void HoldFrequency(double freq);

    /// <summary>
    /// Resumes normal scanning operation after a hold.
    /// </summary>
    void ResumeScan();

    /// <summary>
    /// Starts recording raw IQ data to a file.
    /// </summary>
    /// <param name="label">A label for the filename.</param>
    void StartDumping(string label);

    /// <summary>
    /// Stops recording raw IQ data.
    /// </summary>
    void StopDumping();
}
