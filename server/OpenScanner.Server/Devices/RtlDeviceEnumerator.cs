using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenScanner.Server.Models;

namespace OpenScanner.Server.Devices;

/// <summary>Lists the RTL-SDR dongles currently attached.</summary>
public interface IDeviceEnumerator
{
    /// <summary>Lists attached dongles. Returns an empty list when none are found or the tool is missing.</summary>
    Task<IReadOnlyList<SdrDeviceInfo>> EnumerateAsync(CancellationToken token = default);
}

/// <summary>
/// Lists RTL-SDR dongles by asking <c>rtl_test</c> for a device that doesn't exist. librtlsdr
/// prints every attached device (index, vendor, product, serial) before it looks for a match,
/// then exits when it finds none, so no dongle is ever opened. That matters: opening one that a
/// running capture holds would fail, and listing must work while the scanner is running.
/// </summary>
public partial class RtlDeviceEnumerator : IDeviceEnumerator
{
    // Matches no index (not numeric) and, in practice, no serial as an exact, prefix or suffix
    // match, which are the three ways librtlsdr's device search tries a -d argument.
    private const string NoSuchDevice = "openscanner-enumerate";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<RtlDeviceEnumerator> _logger;

    /// <summary>Initializes a new instance of the <see cref="RtlDeviceEnumerator"/> class.</summary>
    public RtlDeviceEnumerator(ILogger<RtlDeviceEnumerator> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SdrDeviceInfo>> EnumerateAsync(CancellationToken token = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PlatformTools.RtlTest,
            Arguments = $"-d {NoSuchDevice}",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return Array.Empty<SdrDeviceInfo>();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(Timeout);

            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // A librtlsdr that opened a device anyway would sit in its benchmark loop.
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                _logger.LogWarning("rtl_test did not exit while listing devices; using what it printed");
            }

            var output = await SafeRead(stderr) + "\n" + await SafeRead(stdout);
            return Parse(output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not list RTL-SDR devices with rtl_test");
            return Array.Empty<SdrDeviceInfo>();
        }
    }

    private static async Task<string> SafeRead(Task<string> read)
    {
        try { return await read; } catch { return string.Empty; }
    }

    [GeneratedRegex(@"^\s*(\d+):\s+(.*?),\s*SN:\s*(.*?)\s*$", RegexOptions.Multiline)]
    private static partial Regex DeviceLine();

    /// <summary>
    /// Parses librtlsdr's device listing:
    /// <code>
    /// Found 2 device(s):
    ///   0:  Realtek, RTL2838UHIDIR, SN: 00000001
    ///   1:  Nooelec, NESDR SMArt v5, SN: 12345678
    /// </code>
    /// The vendor and product are separated by the first comma; a product may itself contain
    /// commas, which stay in the product.
    /// </summary>
    public static IReadOnlyList<SdrDeviceInfo> Parse(string output)
    {
        var raw = new List<(int Index, string Vendor, string Product, string Serial)>();
        foreach (Match m in DeviceLine().Matches(output))
        {
            var index = int.Parse(m.Groups[1].Value);
            var names = m.Groups[2].Value;
            var comma = names.IndexOf(',');
            var vendor = comma < 0 ? names.Trim() : names[..comma].Trim();
            var product = comma < 0 ? string.Empty : names[(comma + 1)..].Trim();
            raw.Add((index, vendor, product, m.Groups[3].Value.Trim()));
        }

        var serialCounts = raw
            .Where(d => d.Serial.Length > 0)
            .GroupBy(d => d.Serial)
            .ToDictionary(g => g.Key, g => g.Count());

        return raw
            .OrderBy(d => d.Index)
            .Select(d =>
            {
                var unique = d.Serial.Length > 0 && serialCounts[d.Serial] == 1;
                var id = unique ? $"sn:{d.Serial}" : $"idx:{d.Index}";
                return new SdrDeviceInfo(d.Index, d.Vendor, d.Product, d.Serial, id, unique);
            })
            .ToList();
    }
}
