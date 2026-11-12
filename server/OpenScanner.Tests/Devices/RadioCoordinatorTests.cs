using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenScanner.Server.Audio;
using OpenScanner.Server.Devices;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests.Devices;

/// <summary>
/// The coordinator against fake workers: which SDRs run, what restarts when the plan
/// changes, where commands go, and how states merge.
/// </summary>
public class RadioCoordinatorTests : IDisposable
{
    private sealed class FakeWorker : ISdrWorker
    {
        public FakeWorker(string id) => DeviceId = id;
        public string DeviceId { get; }
        public DeviceAssignment? Assignment { get; private set; }
        public int? Index;
        public int AssignCount, StartCount, StopCount, ResumeCount;
        public bool Disposed;
        public readonly List<double> Holds = new();
        public readonly List<double> Avoids = new();
        public ScannerState State = new("IDLE", 0);

        public event Action<ScannerState>? OnStateChanged;
        public event Action<RadioEvent>? OnNewEvent;

        public void Raise(ScannerState s) { State = s; OnStateChanged?.Invoke(s); }
        public void RaiseEvent(RadioEvent e) => OnNewEvent?.Invoke(e);

        public ScannerState GetState() => State;
        public RadioDiagnostics GetDiagnostics() => new(2, 100);
        public void Assign(DeviceAssignment a, int? i) { Assignment = a; Index = i; AssignCount++; }
        public void Start() { StartCount++; Raise(State with { Status = "SCANNING" }); }
        public void Stop() => StopCount++;
        public void SetSquelch(double db) { }
        public void HoldFrequency(double f) { Holds.Add(f); Raise(State with { Status = "MONITORING", ManualHoldFrequency = f }); }
        public void ResumeScan() { ResumeCount++; Raise(State with { Status = "SCANNING", ManualHoldFrequency = null }); }
        public void AvoidFrequency(double f, double d) => Avoids.Add(f);
        public void StartDebugSpectrum(double f, double? g = null) => Raise(State with { Status = "DEBUG" });
        public void StartDumping(string l) { }
        public void StopDumping() { }
        public byte[][] GetPreRollBuffer() => Array.Empty<byte[]>();
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeFactory : ISdrWorkerFactory
    {
        public readonly List<FakeWorker> Created = new();
        public ISdrWorker Create(string id) { var w = new FakeWorker(id); Created.Add(w); return w; }
        public FakeWorker Live(string id) => Created.Last(w => w.DeviceId == id);
    }

    private readonly FakeFactory _factory = new();
    private readonly AudioMixer _mixer = new();
    private readonly RadioCoordinator _radio;

    public RadioCoordinatorTests()
    {
        var db = new Mock<IDatabase>();
        db.Setup(d => d.GetSdrDevicesAsync()).ReturnsAsync(Array.Empty<SdrDeviceConfig>());
        var channels = new Mock<IChannelService>();
        channels.Setup(c => c.Channels).Returns(new List<Channel>());
        var manager = new SdrManager(
            new SdrDeviceService(db.Object, Mock.Of<IDeviceEnumerator>(), NullLogger<SdrDeviceService>.Instance),
            channels.Object, db.Object, NullLogger<SdrManager>.Instance);
        var gps = new Mock<GpsService>(new Mock<ILogger<GpsService>>().Object);

        _radio = new RadioCoordinator(
            manager, _factory, _mixer, Mock.Of<IRecordingService>(), channels.Object, gps.Object,
            NullLogger<RadioCoordinator>.Instance);
    }

    public void Dispose() => _mixer.Dispose();

    private static Channel Ch(double f, string mode = "NFM") => new() { Frequency = f, Mode = mode, AlphaTag = $"{f}" };
    private static SdrDeviceConfig Sdr(string id, double gain = 30) =>
        new() { Id = id, Label = id, GainAnalog = gain, GainDigital = gain, GainMixed = gain };

    private static SdrSnapshot Snapshot(IEnumerable<Channel> channels, params SdrDeviceConfig[] sdrs) =>
        new(Array.Empty<SdrDevice>(), SdrAssignmentMode.Auto,
            SdrPlanner.Plan(channels.ToList(), sdrs, SdrAssignmentMode.Auto),
            sdrs.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => (int?)x.i));

    [Fact]
    public void Apply_StartsAWorkerPerBusySdr_WithItsIndex()
    {
        _radio.Start();
        _radio.Apply(Snapshot(new[] { Ch(155.0), Ch(460.0) }, Sdr("A"), Sdr("B"), Sdr("C")));

        Assert.Equal(new[] { "A", "B" }, _factory.Created.Select(w => w.DeviceId));
        Assert.All(_factory.Created, w => Assert.Equal(1, w.StartCount));
        Assert.Equal(1, _factory.Live("B").Index);
    }

    [Fact]
    public void Apply_BeforeStart_AssignsButDoesNotStart()
    {
        _radio.Apply(Snapshot(new[] { Ch(155.0) }, Sdr("A")));
        Assert.Equal(0, _factory.Live("A").StartCount);

        _radio.Start();
        Assert.Equal(1, _factory.Live("A").StartCount);
    }

    /// <summary>Editing one cluster restarts only the SDR carrying it.</summary>
    [Fact]
    public void Replanning_ReassignsOnlyChangedWorkers()
    {
        _radio.Start();
        var a = Sdr("A");
        var b = Sdr("B");
        _radio.Apply(Snapshot(new[] { Ch(155.0), Ch(460.0) }, a, b));
        _radio.Apply(Snapshot(new[] { Ch(155.0), Ch(460.0), Ch(460.1) }, a, b));

        Assert.Equal(1, _factory.Live("A").AssignCount);
        Assert.Equal(2, _factory.Live("B").AssignCount);
    }

    [Fact]
    public void GainChange_ReassignsTheWorker()
    {
        _radio.Apply(Snapshot(new[] { Ch(155.0) }, Sdr("A", gain: 30)));
        _radio.Apply(Snapshot(new[] { Ch(155.0) }, Sdr("A", gain: 40)));
        Assert.Equal(2, _factory.Live("A").AssignCount);
    }

    [Fact]
    public void SdrLosingAllChannels_IsStopped()
    {
        _radio.Apply(Snapshot(new[] { Ch(155.0), Ch(460.0) }, Sdr("A"), Sdr("B")));
        var b = _factory.Live("B");

        _radio.Apply(Snapshot(new[] { Ch(155.0) }, Sdr("A"), Sdr("B")));

        Assert.True(b.Disposed);
        Assert.DoesNotContain(_radio.GetState().Devices!, d => d.Id == "B" && d.Status != "IDLE");
    }

    [Fact]
    public void Hold_GoesToTheSdrCarryingTheChannel_AndReleasesOtherHolds()
    {
        _radio.Start();
        _radio.Apply(Snapshot(new[] { Ch(155.0), Ch(460.0) }, Sdr("A"), Sdr("B")));

        _radio.HoldFrequency(155.0);
        _radio.HoldFrequency(460.0);

        Assert.Equal(new[] { 155.0 }, _factory.Live("A").Holds);
        Assert.Equal(new[] { 460.0 }, _factory.Live("B").Holds);
        Assert.Equal(1, _factory.Live("A").ResumeCount);
    }

    [Fact]
    public void Avoid_GoesToTheOwner()
    {
        _radio.Apply(Snapshot(new[] { Ch(155.0), Ch(460.0) }, Sdr("A"), Sdr("B")));
        _radio.AvoidFrequency(460.0, 30);
        Assert.Empty(_factory.Live("A").Avoids);
        Assert.Equal(new[] { 460.0 }, _factory.Live("B").Avoids);
    }

    /// <summary>Resume must not restart a FastScan SDR that is simply scanning.</summary>
    [Fact]
    public void Resume_LeavesScanningSdrsAlone()
    {
        _radio.Start();
        _radio.Apply(Snapshot(new[] { Ch(155.0), Ch(460.0) }, Sdr("A"), Sdr("B")));
        _radio.HoldFrequency(460.0);

        _radio.ResumeScan();

        Assert.Equal(0, _factory.Live("A").ResumeCount);
        Assert.Equal(1, _factory.Live("B").ResumeCount);
    }

    [Fact]
    public void State_MergesParallelChannelsAndFocusesTheReceivingSdr()
    {
        _radio.Start();
        _radio.Apply(Snapshot(new[] { Ch(155.0), Ch(155.1), Ch(460.0), Ch(851.0) }, Sdr("A"), Sdr("B")));
        var fast = _factory.Created.Single(w => w.Assignment!.Role == SdrRole.FastScan);
        var hop = _factory.Created.Single(w => w.Assignment!.Role == SdrRole.Hop);

        fast.Raise(fast.State with
        {
            IsHardwareConnected = true,
            ParallelChannels = new[] { new ParallelChannelState(Ch(155.0), false, -90, false, DeviceId: fast.DeviceId) },
        });
        hop.Raise(hop.State with { Status = "RECEIVING", CurrentFrequency = 460.0, SourceID = 42 });

        var state = _radio.GetState();
        Assert.Equal("RECEIVING", state.Status);
        Assert.Equal(460.0, state.CurrentFrequency);
        Assert.Equal(42, state.SourceID);
        Assert.True(state.IsHardwareConnected);
        Assert.Single(state.ParallelChannels!);
        Assert.Equal(new[] { "FastScan", "Hop" }, state.Devices!.Select(d => d.Role).OrderBy(r => r));
    }

    [Fact]
    public void WorkerEvents_AreForwarded()
    {
        _radio.Apply(Snapshot(new[] { Ch(155.0) }, Sdr("A")));
        RadioEvent? seen = null;
        _radio.OnNewEvent += e => seen = e;

        _factory.Live("A").RaiseEvent(new RadioEvent { Type = "TONE_OUT" });

        Assert.Equal("TONE_OUT", seen?.Type);
    }

    [Fact]
    public void DisposedWorker_CannotResurrectItsState()
    {
        _radio.Apply(Snapshot(new[] { Ch(155.0), Ch(460.0) }, Sdr("A"), Sdr("B")));
        var b = _factory.Live("B");
        _radio.Apply(Snapshot(new[] { Ch(155.0) }, Sdr("A"), Sdr("B")));

        b.Raise(b.State with { Status = "RECEIVING" });

        Assert.NotEqual("RECEIVING", _radio.GetState().Status);
    }

    [Fact]
    public void DebugSpectrum_TargetsTheChosenSdr()
    {
        _radio.Apply(Snapshot(new[] { Ch(155.0), Ch(460.0) }, Sdr("A"), Sdr("B")));
        _radio.StartDebugSpectrum(460.0, 30, "B");

        Assert.Equal("DEBUG", _factory.Live("B").State.Status);
        Assert.NotEqual("DEBUG", _factory.Live("A").State.Status);
    }

    /// <summary>
    /// Fast-scanned channels spread across the stereo field by frequency, across all SDRs.
    /// </summary>
    [Fact]
    public void Apply_LaysOutTheMixerAcrossSdrs()
    {
        _radio.Apply(Snapshot(new[] { Ch(155.0), Ch(460.0) }, Sdr("A"), Sdr("B")));

        var low = _mixer.PanFor(155.0);
        var high = _mixer.PanFor(460.0);
        Assert.True(low.Left > low.Right);
        Assert.True(high.Right > high.Left);
    }
}
