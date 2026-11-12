using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using OpenScanner.Server.Audio;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;

namespace OpenScanner.Server.Devices;

/// <summary>
/// The radio the rest of the app sees, built from one <see cref="ISdrWorker"/> per dongle.
///
/// <para>It runs a worker for every SDR the current <see cref="SdrPlan"/> gives channels to.
/// When the plan changes it restarts only the workers whose assignment actually changed. It
/// routes per-channel commands (hold, avoid) to the SDR carrying that channel, and merges the
/// workers' states into one <see cref="ScannerState"/>. Audio from all of them is mixed into
/// one stream by the shared <see cref="AudioMixer"/>.</para>
/// </summary>
public class RadioCoordinator : BackgroundService, IRadioSource
{
    private readonly SdrManager _manager;
    private readonly ISdrWorkerFactory _factory;
    private readonly AudioMixer _mixer;
    private readonly IChannelService _channelService;
    private readonly ILogger<RadioCoordinator> _logger;

    private readonly object _lock = new();
    private readonly Dictionary<string, (ISdrWorker Worker, string Signature)> _workers = new();
    private readonly ConcurrentDictionary<string, ScannerState> _workerStates = new();
    private SdrSnapshot _snapshot = SdrSnapshot.Empty;
    private GpsData? _gps;
    private double? _squelch;
    private bool _started;

    /// <inheritdoc />
    public event Action<ScannerState>? OnStateChanged;

    /// <inheritdoc />
    public event Action<CallLog>? OnNewLog;

    /// <inheritdoc />
    public event Action<AudioChunk>? OnAudio;

    /// <inheritdoc />
    public event Action<RadioEvent>? OnNewEvent;

    /// <summary>Initializes a new instance of the <see cref="RadioCoordinator"/> class.</summary>
    public RadioCoordinator(
        SdrManager manager,
        ISdrWorkerFactory factory,
        AudioMixer mixer,
        IRecordingService recordingService,
        IChannelService channelService,
        GpsService gps,
        ILogger<RadioCoordinator> logger)
    {
        _manager = manager;
        _factory = factory;
        _mixer = mixer;
        _channelService = channelService;
        _logger = logger;

        _mixer.OnAudio += chunk => OnAudio?.Invoke(chunk);
        recordingService.OnNewLog += log => OnNewLog?.Invoke(log);
        gps.OnGpsUpdate += data =>
        {
            _gps = data;
            _channelService.CheckGeoRefresh(data.Lat, data.Lon);
            PublishState();
        };
        _manager.PlanChanged += Apply;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1000, stoppingToken);
        await _manager.WhenReady.WaitAsync(stoppingToken);
        Apply(_manager.Current);
        Start();
    }

    // ---------------------------------------------------------------- planning

    /// <summary>
    /// Brings the running workers in line with a plan. A worker whose assignment is unchanged
    /// keeps running undisturbed; one whose channels, gain, ppm or device index changed
    /// restarts; SDRs with nothing to do are stopped.
    /// </summary>
    internal void Apply(SdrSnapshot snapshot)
    {
        lock (_lock)
        {
            _snapshot = snapshot;
            _mixer.SetLayout(snapshot.Plan.Devices
                .Where(d => d.Role == SdrRole.FastScan)
                .SelectMany(d => d.Channels.Select(c => c.Frequency)));

            var wanted = snapshot.Plan.Devices
                .Where(d => d.Role != SdrRole.Idle)
                .ToDictionary(d => d.Device.Id);

            foreach (var id in _workers.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
            {
                _logger.LogInformation("Stopping SDR {Id}: nothing assigned", id);
                _workers[id].Worker.Dispose();
                _workers.Remove(id);
                _workerStates.TryRemove(id, out _);
            }

            foreach (var (id, assignment) in wanted)
            {
                var index = snapshot.DeviceIndexes.GetValueOrDefault(id);
                var signature = Signature(assignment, index);

                if (!_workers.TryGetValue(id, out var entry))
                {
                    var worker = _factory.Create(id);
                    worker.OnStateChanged += state => OnWorkerState(worker, state);
                    worker.OnNewEvent += e => OnNewEvent?.Invoke(e);
                    if (_squelch is double sq) worker.SetSquelch(sq);
                    worker.Assign(assignment, index);
                    _workers[id] = (worker, signature);
                    _logger.LogInformation("Starting SDR {Id} ({Role}, {Count} channels)", id, assignment.Role, assignment.Channels.Count);
                    if (_started) worker.Start();
                }
                else if (entry.Signature != signature)
                {
                    _logger.LogInformation("Reassigning SDR {Id} ({Role}, {Count} channels)", id, assignment.Role, assignment.Channels.Count);
                    entry.Worker.Assign(assignment, index);
                    _workers[id] = (entry.Worker, signature);
                }
            }
        }
        PublishState();
    }

    /// <summary>
    /// Everything about an assignment that a running capture bakes in at launch. Two
    /// assignments with the same signature can share a worker without a restart.
    /// </summary>
    internal static string Signature(DeviceAssignment a, int? deviceIndex)
    {
        static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        var sb = new StringBuilder()
            .Append(deviceIndex?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|')
            .Append(a.Role).Append('|')
            .Append(N(a.Device.Ppm)).Append('|')
            .Append(N(a.Device.GainAnalog)).Append('/').Append(N(a.Device.GainDigital)).Append('/').Append(N(a.Device.GainMixed));
        foreach (var c in a.Channels.OrderBy(c => c.Frequency))
        {
            sb.Append('|').Append(N(c.Frequency)).Append(':').Append(c.Mode).Append(':').Append(c.Tone)
              .Append(':').Append(c.DmrSlot).Append(':').Append(c.DmrColorCode).Append(':').Append(c.DmrTalkgroup)
              .Append(':').Append(c.AlphaTag);
        }
        return sb.ToString();
    }

    private void OnWorkerState(ISdrWorker worker, ScannerState state)
    {
        // A disposed worker can still finish a state update; don't let it resurrect itself.
        lock (_lock)
        {
            if (!_workers.TryGetValue(worker.DeviceId, out var entry) || !ReferenceEquals(entry.Worker, worker)) return;
        }

        var previous = _workerStates.GetValueOrDefault(worker.DeviceId);
        _workerStates[worker.DeviceId] = state;

        // A dongle that stops delivering may have been unplugged, or replugged at a new
        // index. Look again now rather than at the next poll.
        if (previous?.IsHardwareConnected == true && !state.IsHardwareConnected)
            _manager.RequestRescan();

        PublishState();
    }

    // ---------------------------------------------------------------- state

    private void PublishState() => OnStateChanged?.Invoke(GetState());

    /// <inheritdoc />
    public ScannerState GetState()
    {
        SdrSnapshot snapshot;
        List<ScannerState> states;
        lock (_lock)
        {
            snapshot = _snapshot;
            states = snapshot.Plan.Devices
                .Select(d => _workerStates.GetValueOrDefault(d.Device.Id))
                .OfType<ScannerState>()
                .ToList();
        }

        var devices = DeviceStates(snapshot);
        if (states.Count == 0)
            return new ScannerState("IDLE", 0, Squelch: _squelch, Gps: _gps, Devices: devices);

        // The state the single-channel fields (current channel, IDs, signal) describe: a
        // debug sweep, then an SDR receiving or monitoring, then a hopper, then the first.
        var focus = Focus(states);
        var spectrum = states.FirstOrDefault(s => s.Status == "DEBUG") ?? states.FirstOrDefault(s => s.RfSpectrum != null);
        var parallel = states.Where(s => s.ParallelChannels != null).SelectMany(s => s.ParallelChannels!).ToArray();
        var attached = devices.Where(d => d.Attached).ToList();

        return focus with
        {
            Status = states.Select(s => s.Status).MaxBy(StatusRank) ?? "IDLE",
            IsHardwareConnected = states.Any(s => s.IsHardwareConnected),
            RfSpectrum = spectrum?.RfSpectrum,
            ParallelChannels = parallel.Length > 0 ? parallel : null,
            IsAudioStreaming = states.Any(s => s.IsAudioStreaming),
            Squelch = _squelch ?? focus.Squelch,
            Gps = _gps,
            DeviceName = attached.Count switch
            {
                0 => null,
                1 => attached[0].Product,
                _ => $"{attached.Count} SDRs",
            },
            DevicePort = attached.Count == 0 ? null : string.Join(", ", attached.Select(d => d.Label)),
            Devices = devices,
        };
    }

    private static int StatusRank(string status) => status switch
    {
        "DEBUG" => 5,
        "RECEIVING" => 4,
        "MONITORING" => 3,
        "SCANNING" => 2,
        _ => 1,
    };

    private static ScannerState Focus(List<ScannerState> states) =>
        states.FirstOrDefault(s => s.Status == "DEBUG")
        ?? states.FirstOrDefault(s => s.Status == "RECEIVING")
        ?? states.FirstOrDefault(s => s.Status == "MONITORING")
        ?? states.FirstOrDefault(s => s.CurrentFrequency != null)
        ?? states[0];

    private SdrDeviceState[] DeviceStates(SdrSnapshot snapshot)
    {
        var planned = snapshot.Plan.Devices.ToDictionary(d => d.Device.Id);
        var known = snapshot.Devices.Select(d => d.Config.Id).ToHashSet();

        var rows = snapshot.Devices.Select(d => Row(d.Config, d.Attached != null, planned.GetValueOrDefault(d.Config.Id)));
        // The stand-in default device isn't a stored dongle but still shows while it runs.
        var standIns = snapshot.Plan.Devices.Where(a => !known.Contains(a.Device.Id)).Select(a => Row(a.Device, false, a));
        return rows.Concat(standIns).ToArray();

        SdrDeviceState Row(SdrDeviceConfig config, bool attached, DeviceAssignment? a)
        {
            var state = _workerStates.GetValueOrDefault(config.Id);
            var fast = a?.Role == SdrRole.FastScan ? a.Banks.FirstOrDefault() : null;
            return new SdrDeviceState(
                Id: config.Id,
                Label: config.Label,
                Product: config.Product,
                Serial: config.Serial,
                Attached: attached,
                Enabled: config.Enabled,
                Role: (a?.Role ?? SdrRole.Idle).ToString(),
                Status: state?.Status ?? "IDLE",
                IsHardwareConnected: state?.IsHardwareConnected ?? false,
                GainClass: (a?.GainClass ?? GainClass.Analog).ToString(),
                Gain: state?.Gain ?? a?.GainFor(),
                CenterFrequency: fast?.CenterFrequency,
                CurrentFrequency: a?.Role == SdrRole.Hop ? state?.CurrentFrequency : null,
                Frequencies: a?.Channels.Select(c => c.Frequency).ToArray() ?? Array.Empty<double>());
        }
    }

    /// <inheritdoc />
    public RadioDiagnostics GetDiagnostics()
    {
        var all = Workers().Select(w => w.GetDiagnostics()).ToList();
        return new RadioDiagnostics(all.Sum(d => d.RestartCount), Math.Round(all.Sum(d => d.ThroughputKbps), 1));
    }

    // ---------------------------------------------------------------- commands

    private List<ISdrWorker> Workers()
    {
        lock (_lock)
        {
            // Plan order, so "the first SDR" is stable.
            return _snapshot.Plan.Devices
                .Select(d => _workers.TryGetValue(d.Device.Id, out var e) ? e.Worker : null)
                .OfType<ISdrWorker>()
                .ToList();
        }
    }

    /// <summary>The worker carrying a frequency; otherwise a hopper (it can tune anywhere); otherwise the first.</summary>
    private ISdrWorker? WorkerFor(double freq)
    {
        lock (_lock)
        {
            var owner = _snapshot.Plan.OwnerOf(freq)?.Device.Id
                        ?? _snapshot.Plan.Devices.FirstOrDefault(d => d.Role == SdrRole.Hop)?.Device.Id;
            if (owner != null && _workers.TryGetValue(owner, out var e)) return e.Worker;
        }
        return Workers().FirstOrDefault();
    }

    private ISdrWorker? FocusWorker()
    {
        var workers = Workers();
        if (workers.Count == 0) return null;
        var states = workers.Select(w => w.GetState()).ToList();
        var focus = Focus(states);
        return workers[states.IndexOf(focus)];
    }

    /// <inheritdoc />
    public void ReloadChannels()
    {
        // The reload completes asynchronously and raises ChannelsChanged, which re-plans;
        // only the SDRs whose channels changed then restart.
        _channelService.ReloadChannels();
    }

    /// <inheritdoc />
    public void SetSquelch(double db)
    {
        _squelch = db;
        foreach (var w in Workers()) w.SetSquelch(db);
        PublishState();
    }

    /// <inheritdoc />
    public void SetGain(double db) => _ = RunLogged(_manager.SetGainForAllAsync(db), "set gain");

    /// <inheritdoc />
    public void SetPpm(double ppm) => _ = RunLogged(_manager.SetPpmForAllAsync(ppm), "set ppm");

    private async Task RunLogged(Task task, string what)
    {
        try { await task; }
        catch (Exception ex) { _logger.LogError(ex, "Failed to {What}", what); }
    }

    /// <inheritdoc />
    public void Start()
    {
        lock (_lock) _started = true;
        foreach (var w in Workers()) w.Start();
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_lock) _started = false;
        foreach (var w in Workers()) w.Stop();
    }

    /// <inheritdoc />
    public void HoldFrequency(double freq)
    {
        var target = WorkerFor(freq);
        if (target == null) return;

        // One hold at a time: release a hold another SDR is keeping.
        foreach (var w in Workers().Where(w => w != target && w.GetState().ManualHoldFrequency != null))
            w.ResumeScan();

        target.HoldFrequency(freq);
    }

    /// <inheritdoc />
    public void ResumeScan()
    {
        // Only SDRs that are held, locked on or sweeping. A FastScan SDR that is simply
        // scanning would otherwise restart and drop every channel it is listening to.
        foreach (var w in Workers())
        {
            var s = w.GetState();
            if (s.Status != "SCANNING" || s.ManualHoldFrequency != null) w.ResumeScan();
        }
    }

    /// <inheritdoc />
    public void AvoidFrequency(double freq, double durationSeconds) =>
        WorkerFor(freq)?.AvoidFrequency(freq, durationSeconds);

    /// <inheritdoc />
    public void StartDebugSpectrum(double freq, double? gain = null, string? deviceId = null)
    {
        var workers = Workers();
        var target = workers.FirstOrDefault(w => w.DeviceId == deviceId) ?? workers.FirstOrDefault();
        target?.StartDebugSpectrum(freq, gain);
    }

    /// <inheritdoc />
    public void StartDumping(string label) => FocusWorker()?.StartDumping(label);

    /// <inheritdoc />
    public void StopDumping()
    {
        foreach (var w in Workers()) w.StopDumping();
    }

    /// <inheritdoc />
    public byte[][] GetPreRollBuffer() => FocusWorker()?.GetPreRollBuffer() ?? Array.Empty<byte[]>();

    /// <inheritdoc />
    public override void Dispose()
    {
        lock (_lock)
        {
            foreach (var (w, _) in _workers.Values) w.Dispose();
            _workers.Clear();
        }
        _mixer.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
