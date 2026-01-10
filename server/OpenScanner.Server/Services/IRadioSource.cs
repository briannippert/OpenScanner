using OpenScanner.Server.Models;

namespace OpenScanner.Server.Services;

public interface IRadioSource
{
    event Action<ScannerState>? OnStateChanged;
    event Action<CallLog>? OnNewLog;
    event Action<byte[]>? OnAudio;

    ScannerState GetState();
    void ReloadChannels();
    void SetSquelch(double db);
    void Start();
    void Stop();
    void HoldFrequency(double freq);
    void ResumeScan();
    void StartDumping(string label);
    void StopDumping();
}
