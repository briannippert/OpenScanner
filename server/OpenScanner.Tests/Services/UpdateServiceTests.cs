using OpenScanner.Server.Services;
using Xunit;

namespace OpenScanner.Tests.Services;

public class UpdateServiceTests
{
    [Theory]
    // Newer release available (the pi5 case: running 0.1.242, released v0.1.244).
    [InlineData("0.1.242", "v0.1.244", true)]
    [InlineData("0.1.242", "0.1.243", true)]
    [InlineData("0.1.9", "v0.1.10", true)]
    [InlineData("1.0.0", "v1.0.1", true)]
    // Up to date or ahead → not available.
    [InlineData("0.1.244", "v0.1.244", false)]
    [InlineData("0.1.245", "v0.1.244", false)]
    // Build metadata / prerelease suffixes are ignored.
    [InlineData("0.1.242+abc123", "v0.1.244", true)]
    [InlineData("0.1.244+abc123", "v0.1.244", false)]
    // Missing/garbage inputs → not available.
    [InlineData("", "v0.1.244", false)]
    [InlineData("0.1.244", "", false)]
    public void IsNewerRelease_ComparesVersions(string current, string latestTag, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewerRelease(current, latestTag));
    }
}
