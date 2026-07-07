using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenScanner.Server.Decoders;
using OpenScanner.Server.Interfaces;
using Xunit;

namespace OpenScanner.Tests;

public class DecoderFactoryTests
{
    private readonly IDecoderFactory _factory;

    public DecoderFactoryTests()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
        services.AddTransient<P25>();
        services.AddTransient<DMR>();
        services.AddTransient<NFM>();
        services.AddTransient<AM>();
        services.AddTransient<WFM>();
        services.AddSingleton<IDecoderFactory, DecoderFactory>();
        _factory = services.BuildServiceProvider().GetRequiredService<IDecoderFactory>();
    }

    [Theory]
    [InlineData("P25", typeof(P25))]
    [InlineData("DMR", typeof(DMR))]
    [InlineData("NFM", typeof(NFM))]
    [InlineData("FM", typeof(NFM))]   // FM aliases to NFM
    [InlineData("AM", typeof(AM))]
    [InlineData("WFM", typeof(WFM))]
    public void GetDecoder_MapsModeToType(string mode, Type expected)
    {
        Assert.IsType(expected, _factory.GetDecoder(mode));
    }

    [Theory]
    [InlineData("p25", typeof(P25))]
    [InlineData("am", typeof(AM))]
    public void GetDecoder_IsCaseInsensitive(string mode, Type expected)
    {
        Assert.IsType(expected, _factory.GetDecoder(mode));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something-unknown")]
    public void GetDecoder_UnknownOrNull_DefaultsToNfm(string? mode)
    {
        Assert.IsType<NFM>(_factory.GetDecoder(mode));
    }

    [Fact]
    public void GetDecoder_ReturnsFreshTransientInstances()
    {
        // Decoders are registered transient, so each request is a new instance.
        Assert.NotSame(_factory.GetDecoder("NFM"), _factory.GetDecoder("NFM"));
    }
}
