using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Decoders;

/// <summary>
/// Base class to simplify invoking DSD-FME
/// </summary>
public abstract class DSDBase : IDecoder
{
    protected readonly ILogger _logger;
    private Process? _decoderProcess;
    private CancellationTokenSource? _decodeCts;

    public event Action<byte[]>? OnAudio;
    public event Action<int?, int?, string?>? OnActivity;
    public event Action<string>? OnMetadata;

    public string? InputSource { get; set; }

    protected DSDBase(ILogger logger)
    {
        _logger = logger;
    }

    public abstract string GetCommandLine(Channel channel);

    protected virtual Task OnStarted(CancellationToken token) => Task.CompletedTask;

    public async Task StartAsync(Channel channel, CancellationToken token)
    {
        Stop();
        _decodeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var innerToken = _decodeCts.Token;

        var cmd = GetCommandLine(channel);
        
        var psi = new ProcessStartInfo("bash", $"-c \"{cmd}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        _logger.LogInformation($"Decoder starting: {cmd}");

        try
        {
            _decoderProcess = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start decoder pipeline");
            return;
        }

        if (_decoderProcess == null) return;

        // Handle Audio (Stdout)
        _ = Task.Run(async () => await ProcessAudioStream(_decoderProcess.StandardOutput.BaseStream, innerToken), innerToken);

        // Handle Metadata (Stderr)
        _ = Task.Run(async () => await ProcessMetadataStream(_decoderProcess.StandardError, innerToken), innerToken);
        
        await OnStarted(innerToken);

        try 
        {
             await _decoderProcess.WaitForExitAsync(innerToken);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    // Software pre-amp applied to all decoded audio before sending to clients.
    // rtl_fm output is typically low-level; this compensates without requiring
    // hardware gain increases that can introduce RF noise.
    protected virtual float AudioGain => 3.0f;

    private async Task ProcessAudioStream(Stream stream, CancellationToken token)
    {
        var readBuffer = new byte[4096];
        var sendBuffer = new List<byte>(8192);
        var lastSend = DateTime.UtcNow;
        bool hadData = false;
        float gain = AudioGain;

        try
        {
            while (!token.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(readBuffer, 0, readBuffer.Length, token);
                if (read == 0) break;

                if (!hadData) { _logger.LogInformation("Decoder: Received first audio bytes"); hadData = true; }

                // Apply software gain to raw s16le PCM samples with hard clipping.
                // Process only complete 2-byte samples; carry any odd trailing byte forward.
                int sampleCount = read / 2;
                for (int i = 0; i < sampleCount; i++)
                {
                    int offset = i * 2;
                    short sample = (short)(readBuffer[offset] | (readBuffer[offset + 1] << 8));
                    int amplified = (int)(sample * gain);
                    if (amplified > 32767) amplified = 32767;
                    else if (amplified < -32768) amplified = -32768;
                    short result = (short)amplified;
                    sendBuffer.Add((byte)(result & 0xFF));
                    sendBuffer.Add((byte)((result >> 8) & 0xFF));
                }
                // Pass through any trailing odd byte unchanged (avoids sample boundary misalignment).
                if (read % 2 != 0) sendBuffer.Add(readBuffer[read - 1]);

                bool shouldSend = sendBuffer.Count >= 4096 || 
                                  (sendBuffer.Count > 0 && (DateTime.UtcNow - lastSend).TotalMilliseconds > 40);

                if (shouldSend)
                {
                    var chunk = sendBuffer.ToArray();
                    sendBuffer.Clear();
                    lastSend = DateTime.UtcNow;
                    OnAudio?.Invoke(chunk);
                }
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.LogError(ex, "Decoder audio read error");
        }
    }

    private async Task ProcessMetadataStream(StreamReader reader, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line != null)
                {
                    OnMetadata?.Invoke(line);
                    ParseMetadata(line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error reading decoder metadata");
        }
    }

    protected void RaiseActivity(int? src, int? tgt, string? tone)
    {
        OnActivity?.Invoke(src, tgt, tone);
    }

    protected virtual void ParseMetadata(string line)
    {
        // STRICTER FILTER: Ignore "Sync:" and "P25" to avoid locking on Control Channels (TSBK)
        bool isActivity = 
            line.Contains("Voice") || 
            line.Contains("LDU") || line.Contains("VDU") || // P25 Voice Frames
            line.Contains("HDU") || // P25 Header Data Unit
            line.Contains("TDU") || // P25 Terminator
            (line.Contains("P25") && !line.Contains("TSBK")) || 
            line.Contains("CTCSS") || line.Contains("DCS") || line.Contains("ANALOG") ||
            line.Contains("MDC1200");

        if (isActivity)
        {
            int? src = null;
            int? tgt = null;
            string? tone = null;

            if (line.Contains("MDC1200:"))
            {
                var parts = line.Split("MDC1200:");
                if (parts.Length > 1)
                {
                    var idStr = parts[1].Trim().Split(' ').Last();
                    if (int.TryParse(idStr, System.Globalization.NumberStyles.HexNumber, null, out var s)) src = s;
                }
            }
            else if (line.Contains("Source:"))
            {
                var parts = line.Split("Source:");
                if (parts.Length > 1 && int.TryParse(parts[1].Trim().Split(' ')[0], out var s)) src = s;
            }
            else if (line.Contains("Src:"))
            {
                var parts = line.Split("Src:");
                if (parts.Length > 1 && int.TryParse(parts[1].Trim().Split(' ')[0], out var s)) src = s;
            }

            if (line.Contains("Target:"))
            {
                var parts = line.Split("Target:");
                if (parts.Length > 1 && int.TryParse(parts[1].Trim().Split(' ')[0], out var t)) tgt = t;
            }
            else if (line.Contains("Tgt:"))
            {
                var parts = line.Split("Tgt:");
                if (parts.Length > 1 && int.TryParse(parts[1].Trim().Split(' ')[0], out var t)) tgt = t;
            }

            if (line.Contains("CTCSS:"))
            {
                var parts = line.Split("CTCSS:");
                if (parts.Length > 1) tone = parts[1].Trim().Split(' ')[0] + " Hz";
            }
            else if (line.Contains("DCS:"))
            {
                var parts = line.Split("DCS:");
                if (parts.Length > 1) tone = "D" + parts[1].Trim().Split(' ')[0];
            }

            OnActivity?.Invoke(src, tgt, tone);
        }
    }

    public void Stop()
    {
        _decodeCts?.Cancel();
        try 
        {
            if (_decoderProcess != null && !_decoderProcess.HasExited)
            {
                _decoderProcess.Kill(true); 
            }
        } catch (Exception ex) { _logger.LogDebug(ex, "Failed to kill decoder process"); }
        _decoderProcess = null;
    }
}
