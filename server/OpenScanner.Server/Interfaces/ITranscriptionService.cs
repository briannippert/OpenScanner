using System;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Interfaces;

public interface ITranscriptionService
{
    string? TranscribeAudio(string audioPath);
    void QueueTranscription(CallLog log, string audioPath);
    event Action<CallLog>? OnTranscriptionCompleted;
}