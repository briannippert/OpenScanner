namespace OpenScanner.Server.Models;

/// <summary>
/// Lifecycle of the in-UI updater. Serialized to the client as a lowercase string.
/// </summary>
public enum UpdateState
{
    /// <summary>No update in progress and none known to be available.</summary>
    Idle,
    /// <summary>A version/availability check is running.</summary>
    Checking,
    /// <summary>A newer release is available to install.</summary>
    Available,
    /// <summary>An update is currently being applied.</summary>
    Updating,
    /// <summary>The update built successfully; the service is restarting.</summary>
    Success,
    /// <summary>The update failed; the running build is unchanged and the log is retained.</summary>
    Failed,
}

/// <summary>
/// Snapshot of the updater exposed at <c>GET /api/update/status</c>. The
/// <see cref="State"/> is the lowercase name of <see cref="UpdateState"/> so the
/// client can use it as a discriminated string union.
/// </summary>
public record UpdateStatus
{
    /// <summary>Lowercase <see cref="UpdateState"/> name (idle|checking|available|updating|success|failed).</summary>
    public string State { get; init; } = "idle";

    /// <summary>Currently running app version (nbgv informational version).</summary>
    public string CurrentVersion { get; init; } = "";

    /// <summary>Currently running git commit.</summary>
    public string CurrentCommit { get; init; } = "";

    /// <summary>Tag of the latest GitHub release, if a check has run.</summary>
    public string? LatestTag { get; init; }

    /// <summary>Human-friendly name of the latest release.</summary>
    public string? LatestName { get; init; }

    /// <summary>Release notes (markdown body) of the latest release.</summary>
    public string? ReleaseNotes { get; init; }

    /// <summary>URL of the latest release on GitHub.</summary>
    public string? ReleaseUrl { get; init; }

    /// <summary>Number of commits the running checkout is behind the latest release tag.</summary>
    public int CommitsBehind { get; init; }

    /// <summary>True when the latest release differs from (and is ahead of) the running commit.</summary>
    public bool UpdateAvailable { get; init; }

    /// <summary>Current step of an in-progress update (fetch|reset|build|finalize).</summary>
    public string? Phase { get; init; }

    /// <summary>Accumulated update output (bounded), newest last.</summary>
    public string[] Log { get; init; } = System.Array.Empty<string>();

    /// <summary>Error summary when <see cref="State"/> is failed, or a check error.</summary>
    public string? Error { get; init; }

    /// <summary>ISO-8601 timestamp of the last successful availability check.</summary>
    public string? LastCheckedUtc { get; init; }
}
