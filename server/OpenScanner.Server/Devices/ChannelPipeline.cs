using OpenScanner.Server.DSP;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;

namespace OpenScanner.Server.Devices;

/// <summary>
/// Manages one always-on decoder pipeline for a single channel during parallel FastScan.
/// Owns a Channelizer that isolates the channel from wideband IQ, and either feeds a
/// decoder process (for digital modes like P25/DMR) or uses the FM audio directly
/// (for analog modes like NFM/AM).
/// </summary>
public class ChannelPipeline : IDisposable
{
    private readonly Channel _channel;
    private readonly Channelizer _channelizer;
    private readonly IDecoderFactory _decoderFactory;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly double _squelchDb;

    private IDecoder? _decoder;
    private CancellationTokenSource? _decoderCts;
    private bool _decoderStarted;
    private bool _disposed;

    // Per-channel MDC1200 decoder for analog modes. Each parallel channel needs its
    // own decoder so bursts are attributed to the right channel (a single shared
    // decoder fed interleaved audio can neither decode reliably nor attribute).
    private Mdc1200Decoder? _mdc;

    // Pre-allocated output buffer for channelizer (avoids per-call allocation)
    private byte[] _audioBuffer;

    // Activity state
    private bool _isActive;
    private DateTime _lastActivityTime = DateTime.MinValue;

    // Analog squelch uses pre-demod IQ power from the channelizer.
    // The threshold is calibrated against the IQ magnitude dB scale where
    // noise floor sits around -35 to -45 dB and a carrier reads -5 to -15 dB.
    private const double AnalogSquelchThresholdDb = -28.0;

    // Digital channels are marked inactive after this long without voice audio.
    private const double DigitalHangSeconds = 2.0;
    // 16-bit PCM peak below which a decoder chunk is treated as inter-call filler.
    private const short DigitalSilencePeak = 1200;

    // Signal level metering (read from channelizer's pre-demod IQ power)

    /// <summary>Fires when decoded audio is available.</summary>
    public event Action<Channel, byte[]>? OnAudio;

    /// <summary>Fires when voice/digital activity is detected on this channel.</summary>
    public event Action<Channel, int?, int?, string?>? OnActivity;

    /// <summary>Fires when activity stops (silence timeout).</summary>
    public event Action<Channel>? OnActivityEnded;

    /// <summary>Fires for decoder metadata lines (logging/debug).</summary>
    public event Action<Channel, string>? OnMetadata;

    /// <summary>Whether this channel currently has active audio/voice.</summary>
    public bool IsActive => _isActive;

    /// <summary>Latest measured signal level in dB (IQ power before demod, updated every ~100ms).</summary>
    public double SignalLevelDb => _channelizer.SignalPowerDb;

    /// <summary>The channel this pipeline is assigned to.</summary>
    public Channel Channel => _channel;

    /// <summary>
    /// Creates a channel pipeline for parallel FastScan decoding.
    /// </summary>
    /// <param name="channel">The radio channel to decode</param>
    /// <param name="inputSampleRate">RTL-SDR sample rate in Hz</param>
    /// <param name="outputSampleRate">Audio output rate (typically 48000)</param>
    /// <param name="centerFreqMhz">SDR center frequency in MHz</param>
    /// <param name="decoderFactory">Factory for creating decoders</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="squelchDb">Squelch threshold in dB (for analog modes)</param>
    public ChannelPipeline(
        Channel channel,
        int inputSampleRate,
        int outputSampleRate,
        double centerFreqMhz,
        IDecoderFactory decoderFactory,
        ILogger logger,
        ILoggerFactory loggerFactory,
        double squelchDb = -55.0)
    {
        _channel = channel;
        _decoderFactory = decoderFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _squelchDb = squelchDb;

        // Frequency offset from SDR center (in Hz)
        double offsetHz = (channel.Frequency - centerFreqMhz) * 1e6;

        bool isAm = channel.Mode?.ToUpper() == "AM";
        _channelizer = new Channelizer(inputSampleRate, outputSampleRate, offsetHz, isAm);

        // Pre-allocate audio buffer (generous size for typical IQ chunk of 65536 bytes)
        _audioBuffer = new byte[_channelizer.MaxOutputBytes(65536 * 2)];
    }

    /// <summary>
    /// Start the decoder process (for digital modes) or prepare for direct audio (analog modes).
    /// </summary>
    public void Start(CancellationToken token)
    {
        if (_decoderStarted) return;
        _decoderStarted = true;

        _decoderCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        bool needsDecoder = NeedsDecoderProcess(_channel.Mode);

        if (needsDecoder)
        {
            StartDecoderProcess(_decoderCts.Token);
        }
        else
        {
            // Analog channel: decode MDC1200 unit IDs from the demodulated FM audio and
            // surface them as this channel's source ID via the normal activity path.
            _mdc = new Mdc1200Decoder(_loggerFactory.CreateLogger<Mdc1200Decoder>());
            _mdc.OnPacket += pkt =>
                OnActivity?.Invoke(_channel, pkt.UnitId, null, pkt.IsEmergency ? "EMRG" : null);
        }

        _logger.LogInformation(
            $"ChannelPipeline started: {_channel.AlphaTag} ({_channel.Frequency} MHz) " +
            $"[{_channel.Mode}] decoder={needsDecoder}");
    }

    /// <summary>
    /// Process a chunk of wideband IQ data. Channelizes to this channel's frequency,
    /// feeds to the decoder (if digital), and monitors for activity.
    /// </summary>
    public void ProcessIQ(byte[] iq, int length)
    {
        if (_disposed) return;

        // Ensure buffer is large enough
        int maxOut = _channelizer.MaxOutputBytes(length);
        if (_audioBuffer.Length < maxOut)
            _audioBuffer = new byte[maxOut];

        int audioBytes = _channelizer.ProcessIQ(iq, length, _audioBuffer);
        if (audioBytes <= 0) return;

        bool needsDecoder = NeedsDecoderProcess(_channel.Mode);

        if (needsDecoder)
        {
            // Feed channelized FM audio to the decoder process stdin
            if (_decoder?.CanFeedInput == true)
            {
                _decoder.FeedInput(_audioBuffer, 0, audioBytes);
            }

            // Digital activity is refreshed by the decoder callbacks (voice audio /
            // OnActivity). dsd-fme keeps emitting audio between calls, so without an
            // idle reset here the channel would read "active" forever. This runs on
            // every IQ chunk, so it fires even when the decoder has gone quiet.
            if (_isActive && (DateTime.UtcNow - _lastActivityTime).TotalSeconds > DigitalHangSeconds)
            {
                _isActive = false;
                OnActivityEnded?.Invoke(_channel);
            }
        }
        else
        {
            // Analog mode: channelizer output IS the audio.
            // Use pre-demod IQ power for squelch (post-demod FM noise is always loud).
            bool hasSignal = _channelizer.SignalPowerDb > AnalogSquelchThresholdDb;

            if (hasSignal)
            {
                if (!_isActive)
                {
                    _isActive = true;
                    OnActivity?.Invoke(_channel, null, null, null);
                }
                _lastActivityTime = DateTime.UtcNow;

                // Emit audio only when carrier is present (squelch gate)
                var chunk = new byte[audioBytes];
                Buffer.BlockCopy(_audioBuffer, 0, chunk, 0, audioBytes);
                OnAudio?.Invoke(_channel, chunk);

                // Feed the same carrier audio to this channel's MDC1200 decoder so a
                // PTT/emergency burst is attributed to this channel.
                _mdc?.ProcessAudio(chunk);
            }
            else if (_isActive && (DateTime.UtcNow - _lastActivityTime).TotalSeconds > 2.0)
            {
                _isActive = false;
                OnActivityEnded?.Invoke(_channel);
            }
        }
    }

    /// <summary>
    /// Stop and clean up the pipeline.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _decoderCts?.Cancel();
        _decoder?.Stop();
        _decoder = null;
        _mdc = null;
        _decoderCts?.Dispose();
    }

    // --- Private helpers ---

    /// <summary>
    /// True if a 16-bit LE PCM chunk contains audio above the silence floor
    /// (i.e. actual decoded voice, not dsd-fme's inter-call filler).
    /// </summary>
    private static bool HasVoiceSignal(byte[] chunk)
    {
        // Sample sparsely — we only need to know if any sample is loud.
        for (int i = 0; i + 1 < chunk.Length; i += 8)
        {
            short sample = (short)(chunk[i] | (chunk[i + 1] << 8));
            if (sample > DigitalSilencePeak || sample < -DigitalSilencePeak) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether this mode requires a decoder process (dsd-fme).
    /// P25 and DMR need digital voice decoding; NFM/AM/WFM are decoded by the channelizer.
    /// </summary>
    private static bool NeedsDecoderProcess(string? mode)
    {
        return mode?.ToUpper() switch
        {
            "P25" => true,
            "DMR" => true,
            _ => false
        };
    }

    private void StartDecoderProcess(CancellationToken token)
    {
        _decoder = _decoderFactory.GetDecoder(_channel.Mode);

        // Set InputSource to "cat -" so the decoder reads from stdin instead of rtl_fm.
        // This enables us to feed channelized audio via FeedInput().
        _decoder.InputSource = "cat -";

        _decoder.OnAudio += (chunk) =>
        {
            if (_disposed) return;

            // Only treat non-silent audio as activity — dsd-fme emits low-level
            // filler between calls, which must not keep the channel "active".
            if (HasVoiceSignal(chunk))
            {
                _isActive = true;
                _lastActivityTime = DateTime.UtcNow;
            }

            OnAudio?.Invoke(_channel, chunk);
        };

        _decoder.OnActivity += (src, tgt, tone) =>
        {
            if (_disposed) return;

            _isActive = true;
            _lastActivityTime = DateTime.UtcNow;
            OnActivity?.Invoke(_channel, src, tgt, tone);
        };

        _decoder.OnMetadata += (line) =>
        {
            OnMetadata?.Invoke(_channel, line);
        };

        // Start the decoder process asynchronously
        Task.Run(async () =>
        {
            try
            {
                await _decoder.StartAsync(_channel, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Decoder error for {_channel.AlphaTag}");
            }
        }, token);
    }

}
