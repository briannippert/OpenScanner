using OpenScanner.Server.DSP;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;

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
    private readonly double _squelchDb;

    private IDecoder? _decoder;
    private CancellationTokenSource? _decoderCts;
    private bool _decoderStarted;
    private bool _disposed;

    // Pre-allocated output buffer for channelizer (avoids per-call allocation)
    private byte[] _audioBuffer;

    // Activity state
    private bool _isActive;
    private DateTime _lastActivityTime = DateTime.MinValue;

    // Analog squelch uses pre-demod IQ power from the channelizer.
    // The threshold is calibrated against the IQ magnitude dB scale where
    // noise floor sits around -35 to -45 dB and a carrier reads -5 to -15 dB.
    private const double AnalogSquelchThresholdDb = -28.0;

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
        double squelchDb = -55.0)
    {
        _channel = channel;
        _decoderFactory = decoderFactory;
        _logger = logger;
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
        _decoderCts?.Dispose();
    }

    // --- Private helpers ---

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

            if (!_isActive)
            {
                _isActive = true;
            }
            _lastActivityTime = DateTime.UtcNow;

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
