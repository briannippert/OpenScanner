using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenScanner.Server.Audio;
using OpenScanner.Server.Decoders;
using OpenScanner.Server.Devices;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests.Devices;

/// <summary>
/// The single-channel (lock-on) receive path of <see cref="RtlDevice"/>, driven by the off-air
/// P25 and DMR captures: hold a channel -> lock on -> start the real decoder -> dsd-fme
/// activity -> recording start, ID updates and stop, plus the scanner state the UI sees.
///
/// The decoder normally runs its own rtl_fm; here its input source is the capture, already
/// channelized to the same 48 kHz discriminator stream (see
/// <see cref="OffAirFixtures.WriteDiscriminatorFile"/>). Everything downstream of rtl_fm is the
/// production code. The talkgroup-hold regression (DMR channels with a talkgroup recorded
/// silence) lived in exactly this path, which had no tests.
/// </summary>
public class LockOnDecodeTests
{
    [Fact]
    public async Task P25_HoldRecordsOneTransmissionWithIdsAndVoice()
    {
        if (!OffAirFixtures.DecoderToolsAvailable()) return;

        var channel = new Channel { Frequency = OffAirFixtures.ChannelMhz, Mode = "P25", AlphaTag = "P25 TEST" };
        var r = await HoldAndDecode(OffAirFixtures.P25, channel);

        var rec = Assert.Single(r.Recordings.Started);
        Assert.Same(channel, rec.Channel);
        Assert.Equal((OffAirFixtures.SourceId, OffAirFixtures.P25Talkgroup), r.Recordings.IdsFor(rec));
        Assert.Equal([(channel.Frequency, (string?)null)], r.Recordings.Stopped);
        Assert.True(r.Recordings.AudioSeconds >= 3.0, $"recording got only {r.Recordings.AudioSeconds:F1}s of audio");

        Assert.Equal(OffAirFixtures.SourceId, r.FinalState.SourceID);
        Assert.Equal(OffAirFixtures.P25Talkgroup, r.FinalState.TargetID);
        Assert.Contains(r.States, s => s.Status == "RECEIVING");
        // Held, so after the call ends the device returns to monitoring rather than scanning.
        Assert.Equal("MONITORING", r.FinalState.Status);
        Assert.Null(r.FinalState.CurrentTone);
    }

    [Theory]
    [InlineData(1, 9, 763901)]          // the channel exactly as configured on the bench
    [InlineData(null, null, null)]      // no filters
    public async Task DmrSimplex_HoldRecordsOneTransmissionWithIdsAndVoice(int? slot, int? cc, int? tg)
    {
        if (!OffAirFixtures.DecoderToolsAvailable()) return;

        var channel = DmrChannel(slot, cc, tg);
        var r = await HoldAndDecode(OffAirFixtures.Dmr, channel);

        var rec = Assert.Single(r.Recordings.Started);
        Assert.Equal((OffAirFixtures.SourceId, OffAirFixtures.DmrTalkgroup), r.Recordings.IdsFor(rec));
        Assert.Single(r.Recordings.Stopped);
        // A talkgroup filter must not mute the audio (the -I TG hold regression).
        Assert.True(r.Recordings.AudioSeconds >= 3.5, $"recording got only {r.Recordings.AudioSeconds:F1}s of audio");
        Assert.True(r.Recordings.LoudSeconds >= 1.0, $"recording is near-silent ({r.Recordings.LoudSeconds:F1}s loud)");

        Assert.Equal(OffAirFixtures.SourceId, r.FinalState.SourceID);
        Assert.Equal(OffAirFixtures.DmrTalkgroup, r.FinalState.TargetID);
        Assert.Equal("MONITORING", r.FinalState.Status);
    }

    /// <summary>
    /// The scanner locks on after it hears a signal, so it joins calls already in progress and
    /// misses the voice header that carries the talkgroup. The recording must still get the
    /// IDs (from the embedded LC) and the voice. With dsd-fme's TG hold (-I) a talkgroup-filtered
    /// channel stayed muted until the embedded LC decoded: 3.6 s of audio here instead of 4.3.
    /// </summary>
    [Fact]
    public async Task DmrSimplex_JoinedMidCall_WithTalkgroupFilter_KeepsTheVoice()
    {
        if (!OffAirFixtures.DecoderToolsAvailable()) return;

        // The call starts ~0.7 s into the capture; 1.0 s joins it after the voice header.
        var r = await HoldAndDecode(OffAirFixtures.Dmr, DmrChannel(1, 9, 763901), skipSeconds: 1.0);

        var rec = Assert.Single(r.Recordings.Started);
        Assert.Equal((OffAirFixtures.SourceId, OffAirFixtures.DmrTalkgroup), r.Recordings.IdsFor(rec));
        Assert.True(r.Recordings.AudioSeconds >= 4.0, $"recording got only {r.Recordings.AudioSeconds:F1}s of audio");
    }

    [Theory]
    [InlineData(2, null, null)]     // simplex is always slot 1
    [InlineData(1, 3, null)]        // co-channel user on another color code
    [InlineData(1, null, 1)]        // another talkgroup
    public async Task DmrSimplex_FilteredOut_StartsNoRecording(int? slot, int? cc, int? tg)
    {
        if (!OffAirFixtures.DecoderToolsAvailable()) return;

        var r = await HoldAndDecode(OffAirFixtures.Dmr, DmrChannel(slot, cc, tg));

        Assert.Empty(r.Recordings.Started);
        Assert.DoesNotContain(r.States, s => s.Status == "RECEIVING");
        Assert.Null(r.FinalState.SourceID);
    }

    // ---- Harness ----

    private static Channel DmrChannel(int? slot, int? cc, int? tg) => new()
    {
        Frequency = OffAirFixtures.ChannelMhz, Mode = "DMR", AlphaTag = "DMR TEST",
        DmrSlot = slot, DmrColorCode = cc, DmrTalkgroup = tg,
    };

    private sealed record Result(FakeRecordingService Recordings, List<ScannerState> States, ScannerState FinalState);

    private static async Task<Result> HoldAndDecode(string fixture, Channel channel, double skipSeconds = 0)
    {
        var disc = OffAirFixtures.WriteDiscriminatorFile(fixture, channel.Mode!, skipSeconds);
        try
        {
            var db = new Mock<IDatabase>();
            db.Setup(d => d.GetAllFireTonesAsync()).ReturnsAsync([]);
            var channels = new Mock<IChannelService>();
            channels.Setup(c => c.Channels).Returns([channel]);
            var recordings = new FakeRecordingService();
            var decoders = new FileFedDecoderFactory(disc);

            using var device = new RtlDevice(
                db.Object, NullLogger<RtlDevice>.Instance, NullLoggerFactory.Instance,
                new ToneDetector(db.Object, NullLogger<ToneDetector>.Instance),
                new Mdc1200Decoder(NullLogger<Mdc1200Decoder>.Instance),
                decoders, recordings, channels.Object, new AudioMixer());

            var states = new List<ScannerState>();
            device.OnStateChanged += s => { lock (states) states.Add(s); };

            device.HoldFrequency(channel.Frequency);

            // The decoder reads the whole capture faster than real time and exits. A recording,
            // if one starts, stops 2 s after the last audio; then nothing more happens.
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(200);
                if (decoders.Started && recordings.QuietFor > TimeSpan.FromSeconds(3)) break;
            }

            Assert.True(decoders.Started, "the lock-on path never started a decoder");
            lock (states) return new Result(recordings, [.. states], device.GetState());
        }
        finally
        {
            File.Delete(disc);
        }
    }

    /// <summary>Real P25/DMR decoders whose input is the capture file instead of rtl_fm.</summary>
    private sealed class FileFedDecoderFactory(string discriminatorFile) : IDecoderFactory
    {
        public volatile bool Started;

        public IDecoder GetDecoder(string? mode)
        {
            DSDBase decoder = mode switch
            {
                "P25" => new P25(NullLogger<P25>.Instance),
                "DMR" => new DMR(NullLogger<DMR>.Instance),
                _ => throw new ArgumentException(mode),
            };
            decoder.InputSource = $"cat '{discriminatorFile}'";
            Started = true;
            return decoder;
        }
    }

    /// <summary>Records what the device asks of the recording service.</summary>
    private sealed class FakeRecordingService : IRecordingService
    {
        private readonly object _gate = new();
        private readonly HashSet<double> _active = [];
        private readonly Stopwatch _sinceLastCall = Stopwatch.StartNew();
        private long _audioBytes, _loudBytes;

        public sealed record Start(Channel Channel, int? Src, int? Tgt);

        public List<Start> Started { get; } = [];
        public List<(double Frequency, int? Src, int? Tgt)> IdUpdates { get; } = [];
        public List<(double Frequency, string? Tone)> Stopped { get; } = [];

        public double AudioSeconds { get { lock (_gate) return _audioBytes / 96000.0; } }
        public double LoudSeconds { get { lock (_gate) return _loudBytes / 96000.0; } }
        public TimeSpan QuietFor { get { lock (_gate) return _sinceLastCall.Elapsed; } }

        /// <summary>The IDs a recording ended up with: from its start, or filled in late.</summary>
        public (int? Src, int? Tgt) IdsFor(Start rec)
        {
            lock (_gate)
            {
                int? src = rec.Src, tgt = rec.Tgt;
                foreach (var u in IdUpdates.Where(u => u.Frequency == rec.Channel.Frequency))
                {
                    src ??= u.Src;
                    tgt ??= u.Tgt;
                }
                return (src, tgt);
            }
        }

        public void StartParallelRecording(Channel channel, int? src, int? tgt, LinkedList<byte[]>? preRoll)
        {
            lock (_gate)
            {
                Started.Add(new Start(channel, src, tgt));
                _active.Add(channel.Frequency);
                foreach (var chunk in preRoll ?? []) CountAudio(chunk);
                _sinceLastCall.Restart();
            }
        }

        public void ProcessAudio(double frequency, byte[] audio)
        {
            lock (_gate)
            {
                if (_active.Contains(frequency)) CountAudio(audio);
                _sinceLastCall.Restart();
            }
        }

        public void UpdateSourceTarget(double frequency, int? src, int? tgt)
        {
            lock (_gate) { IdUpdates.Add((frequency, src, tgt)); _sinceLastCall.Restart(); }
        }

        public void StopParallelRecording(double frequency, string? lastDetectedTone)
        {
            lock (_gate)
            {
                if (_active.Remove(frequency)) Stopped.Add((frequency, lastDetectedTone));
                _sinceLastCall.Restart();
            }
        }

        public bool IsChannelRecording(double frequency) { lock (_gate) return _active.Contains(frequency); }
        public string? RecordingIdFor(double frequency) => IsChannelRecording(frequency) ? $"log_{frequency}" : null;

        private void CountAudio(byte[] chunk)
        {
            _audioBytes += chunk.Length;
            if (OffAirFixtures.IsLoud(chunk)) _loudBytes += chunk.Length;
        }

        // Legacy single-recording API and speaker chain: unused by this path.
        public event Action<CallLog>? OnNewLog { add { } remove { } }
        public bool IsRecording => false;
        public string? CurrentRecordingId => null;
        public int ActiveRecordingCount { get { lock (_gate) return _active.Count; } }
        public IReadOnlyCollection<string> ActiveRecordingIds => [];
        public void StartRecording(Channel channel, int? src, int? tgt, LinkedList<byte[]> preRollBuffer) { }
        public void UpdateSourceTarget(int? src, int? tgt) { }
        public void AppendSpeaker(int src) { }
        public void AppendSpeaker(double frequency, int src) { }
        public void StopRecording(Channel channel, string? lastDetectedTone) { }
        public void ProcessAudio(byte[] audio) { }
    }
}
