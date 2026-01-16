using OpenScanner.Server.Models;

namespace OpenScanner.Server.Interfaces;

public interface IRecordingService
{
    event Action<CallLog>? OnNewLog;
    bool IsRecording { get; }
    void StartRecording(Channel channel, int? src, int? tgt, LinkedList<byte[]> preRollBuffer);
    void StopRecording(Channel channel, string? lastDetectedTone);
    void ProcessAudio(byte[] audio);
}
