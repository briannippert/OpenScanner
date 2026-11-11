using System.Runtime.InteropServices;
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

    [Theory]
    [InlineData(true, Architecture.Arm64, "openscanner-linux-arm64.tar.gz")]
    [InlineData(true, Architecture.X64, "openscanner-linux-x64.tar.gz")]
    // No artifact is published for 32-bit ARM or for non-Linux hosts.
    [InlineData(true, Architecture.Arm, null)]
    [InlineData(false, Architecture.Arm64, null)]
    [InlineData(false, Architecture.X64, null)]
    public void AssetNameFor_MapsPlatformToArtifact(bool isLinux, Architecture arch, string? expected)
    {
        Assert.Equal(expected, UpdateService.AssetNameFor(isLinux, arch));
    }

    private const string HashA = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string HashB = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    [Fact]
    public void FindChecksum_FindsTheNamedFile()
    {
        var sums = $"{HashA}  openscanner-linux-arm64.tar.gz\n{HashB}  openscanner-linux-x64.tar.gz\n";
        Assert.Equal(HashA, UpdateService.FindChecksum(sums, "openscanner-linux-arm64.tar.gz"));
        Assert.Equal(HashB, UpdateService.FindChecksum(sums, "openscanner-linux-x64.tar.gz"));
    }

    [Fact]
    public void FindChecksum_HandlesBinaryMarkerCrlfAndUppercase()
    {
        var sums = $"{HashA.ToUpperInvariant()} *openscanner-linux-x64.tar.gz\r\n";
        Assert.Equal(HashA, UpdateService.FindChecksum(sums, "openscanner-linux-x64.tar.gz"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    // Listed under a different name — a suffix match must not count.
    [InlineData(HashA + "  not-openscanner-linux-x64.tar.gz")]
    // Truncated or non-hex digests are rejected rather than trusted.
    [InlineData("0123abcd  openscanner-linux-x64.tar.gz")]
    [InlineData("zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  openscanner-linux-x64.tar.gz")]
    public void FindChecksum_ReturnsNullWhenMissingOrMalformed(string? sums)
    {
        Assert.Null(UpdateService.FindChecksum(sums, "openscanner-linux-x64.tar.gz"));
    }

    [Fact]
    public void ParseRelease_ReadsTagAndAssets()
    {
        const string json = """
        {
          "tag_name": "v0.1.300",
          "name": "Release 0.1.300",
          "body": "notes",
          "html_url": "https://github.com/briannippert/OpenScanner/releases/tag/v0.1.300",
          "assets": [
            { "name": "SHA256SUMS", "browser_download_url": "https://example.test/SHA256SUMS" },
            { "name": "openscanner-linux-arm64.tar.gz", "browser_download_url": "https://example.test/arm64" }
          ]
        }
        """;
        var release = UpdateService.ParseRelease(json);

        Assert.Equal("v0.1.300", release.Tag);
        Assert.Equal("Release 0.1.300", release.Name);
        Assert.Equal("notes", release.Notes);
        Assert.Equal("https://example.test/arm64", release.Assets["openscanner-linux-arm64.tar.gz"]);
        Assert.True(release.Assets.ContainsKey("SHA256SUMS"));
        Assert.False(release.Assets.ContainsKey("openscanner-linux-x64.tar.gz"));
    }

    [Fact]
    public void ParseRelease_ToleratesAReleaseWithNoAssets()
    {
        // Every release before the artifact pipeline looks like this.
        var release = UpdateService.ParseRelease("""{ "tag_name": "v0.1.271", "assets": [] }""");

        Assert.Equal("v0.1.271", release.Tag);
        Assert.Empty(release.Assets);
    }
}
