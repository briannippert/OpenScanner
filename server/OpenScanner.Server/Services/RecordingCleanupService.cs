using OpenScanner.Server.Interfaces;

namespace OpenScanner.Server.Services;

/// <summary>
/// Background service that guards against the host disk filling up with
/// recordings. When free disk space on the recordings volume drops below a
/// configurable threshold (default 2 GB), it deletes the oldest non-favorite
/// recordings (file + database row) until free space recovers.
/// </summary>
public class RecordingCleanupService : BackgroundService
{
    private const long DefaultMinFreeBytes = 2L * 1024 * 1024 * 1024; // 2 GB
    private const int DeleteBatchSize = 25;
    private const int MaxBatchesPerTick = 40; // safety cap: up to 1000 deletions per tick

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    private readonly IDatabase _db;
    private readonly ILogger<RecordingCleanupService> _logger;
    private readonly long _minFreeBytes;
    private readonly string _recordingsPath;

    public RecordingCleanupService(IDatabase db, ILogger<RecordingCleanupService> logger, IConfiguration config)
    {
        _db = db;
        _logger = logger;

        var configured = config.GetValue<long?>("Recording:MinFreeDiskBytes");
        _minFreeBytes = configured is > 0 ? configured.Value : DefaultMinFreeBytes;

        // Mirror the recordings location used by RecordingService.
        _recordingsPath = Path.GetFullPath(
            Path.Combine(Directory.GetCurrentDirectory(), "../../data/recordings"));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "RecordingCleanupService started. Threshold: {ThresholdMB} MB free at {Path}",
            _minFreeBytes / 1024 / 1024, _recordingsPath);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
                await EnforceFreeSpaceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during recording cleanup tick");
            }
        }
    }

    private async Task EnforceFreeSpaceAsync(CancellationToken stoppingToken)
    {
        var free = GetFreeBytes();
        if (free < 0 || free >= _minFreeBytes) return;

        _logger.LogWarning(
            "Low disk space: {FreeMB} MB free, below {ThresholdMB} MB threshold. Purging oldest recordings.",
            free / 1024 / 1024, _minFreeBytes / 1024 / 1024);

        var totalDeleted = 0;

        for (var batch = 0; batch < MaxBatchesPerTick; batch++)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var ids = (await _db.GetOldestTransmissionIdsAsync(DeleteBatchSize)).ToList();
            if (ids.Count == 0)
            {
                _logger.LogWarning("No more non-favorite recordings to delete; free space still low.");
                break;
            }

            foreach (var id in ids)
            {
                await _db.DeleteTransmissionAsync(id);
                totalDeleted++;
            }

            free = GetFreeBytes();
            if (free < 0 || free >= _minFreeBytes) break;
        }

        if (totalDeleted > 0)
        {
            _logger.LogInformation(
                "Purged {Count} recording(s). Free space now {FreeMB} MB.",
                totalDeleted, GetFreeBytes() / 1024 / 1024);
        }
    }

    /// <summary>
    /// Available free bytes on the recordings volume, or -1 if it cannot be read.
    /// </summary>
    private long GetFreeBytes()
    {
        try
        {
            var probe = Directory.Exists(_recordingsPath)
                ? _recordingsPath
                : Directory.GetCurrentDirectory();
            return new DriveInfo(probe).AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read free disk space for {Path}", _recordingsPath);
            return -1;
        }
    }
}
