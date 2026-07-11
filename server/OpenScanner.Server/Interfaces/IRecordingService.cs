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

    /// <summary>
    /// The transmission log id (<c>log_{startTime}</c>) of the currently active
    /// recording, or the most recently started one in parallel mode. Null when no
    /// recording is active. Matches the id assigned to the finalized <see cref="CallLog"/>,
    /// so callers can link concurrent events to the recording they coincide with.
    /// </summary>
    string? CurrentRecordingId { get; }

    /// <summary>Number of recordings currently in flight (parallel channels + legacy single).</summary>
    int ActiveRecordingCount { get; }

    /// <summary>Ids of the recordings currently in flight, newest first.</summary>
    IReadOnlyCollection<string> ActiveRecordingIds { get; }

    void StartRecording(Channel channel, int? src, int? tgt, LinkedList<byte[]> preRollBuffer);
    void UpdateSourceTarget(int? src, int? tgt);
    void UpdateSourceTarget(double frequency, int? src, int? tgt);
    void AppendSpeaker(int src);
    void AppendSpeaker(double frequency, int src);
    void StopRecording(Channel channel, string? lastDetectedTone);
    void ProcessAudio(byte[] audio);
    void ProcessAudio(double frequency, byte[] audio);
}
