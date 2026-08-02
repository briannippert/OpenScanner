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
    /// Event triggered when a signaling event (fire tone-out or MDC1200) is detected.
    /// </summary>
    event Action<RadioEvent>? OnNewEvent;

    /// <summary>
    /// Gets the current state of the scanner.
    /// </summary>
    /// <returns>The current <see cref="ScannerState"/>.</returns>
    ScannerState GetState();

    /// <summary>
    /// Gets SDR reliability diagnostics (capture restart count and throughput).
    /// </summary>
    RadioDiagnostics GetDiagnostics();

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
    /// Sets the RTL-SDR tuner gain, in dB. A value of 0 selects the tuner's automatic gain (AGC).
    /// The setting is persisted and applied to subsequent captures.
    /// </summary>
    /// <param name="db">The tuner gain in dB, or 0 for AUTO.</param>
    void SetGain(double db);

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
    /// Starts a high-bandwidth RF spectrum debug mode.
    /// </summary>
    /// <param name="freq">The center frequency in MHz.</param>
    /// <param name="gain">The hardware gain (dB).</param>
    void StartDebugSpectrum(double freq, double? gain = null);

    /// <summary>
    /// Temporarily avoids a frequency for a specified duration.
    /// </summary>
    /// <param name="freq">The frequency in MHz.</param>
    /// <param name="durationSeconds">Duration to avoid in seconds.</param>
    void AvoidFrequency(double freq, double durationSeconds);

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

    /// <summary>
    /// Gets the current pre-roll audio buffer (audio data from the start of the current transmission).
    /// </summary>
    /// <returns>Array of audio byte arrays representing the pre-roll buffer.</returns>
    byte[][] GetPreRollBuffer();
}
