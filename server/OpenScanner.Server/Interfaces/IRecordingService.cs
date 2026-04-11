using OpenScanner.Server.Models;

namespace OpenScanner.Server.Interfaces;

public interface IRecordingService
{
    event Action<CallLog>? OnNewLog;

    /// <summary>
    /// Whether any recording is active (legacy single-channel check).
    /// </summary>
    bool IsRecording { get; }

    /// <summary>
    /// Whether a specific channel (by frequency) is currently recording in parallel mode.
    /// </summary>
    bool IsChannelRecording(double frequency);

    void StartRecording(Channel channel, int? src, int? tgt, LinkedList<byte[]> preRollBuffer);
    void UpdateSourceTarget(int? src, int? tgt);
    void UpdateSourceTarget(double frequency, int? src, int? tgt);
    void AppendSpeaker(int src);
    void AppendSpeaker(double frequency, int src);
    void StopRecording(Channel channel, string? lastDetectedTone);
    void ProcessAudio(byte[] audio);
    void ProcessAudio(double frequency, byte[] audio);
}
