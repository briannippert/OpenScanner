using OpenScanner.Server.Models;

namespace OpenScanner.Server.Devices;

/// <summary>
/// Simulated dongles for the Mock radio provider, so the SDR settings and channel
/// assignment UI can be exercised without hardware. <c>Radio:MockDeviceCount</c> sets how many
/// (default 1).
/// </summary>
public class MockDeviceEnumerator : IDeviceEnumerator
{
    private readonly int _count;

    /// <summary>Initializes a new instance of the <see cref="MockDeviceEnumerator"/> class.</summary>
    public MockDeviceEnumerator(IConfiguration configuration)
    {
        _count = Math.Clamp(configuration.GetValue("Radio:MockDeviceCount", 1), 0, 8);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SdrDeviceInfo>> EnumerateAsync(CancellationToken token = default)
    {
        IReadOnlyList<SdrDeviceInfo> devices = Enumerable.Range(0, _count)
            .Select(i => new SdrDeviceInfo(i, "Mock", "RTL2838UHIDIR", $"MOCK{i:0000}", $"sn:MOCK{i:0000}", true))
            .ToList();
        return Task.FromResult(devices);
    }
}
