namespace OpenScanner.Server.Interfaces;

public interface ITranscriptionService
{
    string? TranscribeAudio(string audioPath);
}