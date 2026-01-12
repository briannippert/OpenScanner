using OpenScanner.Server.Interfaces;

namespace OpenScanner.Server.Decoders;

public class DecoderFactory : IDecoderFactory
{
    private readonly IServiceProvider _serviceProvider;

    public DecoderFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public IDecoder GetDecoder(string? mode)
    {
        return (mode?.ToUpper()) switch
        {
            "P25" => _serviceProvider.GetRequiredService<P25>(),
            "NFM" => _serviceProvider.GetRequiredService<NFM>(),
            "FM" => _serviceProvider.GetRequiredService<NFM>(),
            "AM" => _serviceProvider.GetRequiredService<AM>(),
            "WFM" => _serviceProvider.GetRequiredService<WFM>(),
            _ => _serviceProvider.GetRequiredService<NFM>()
        };
    }
}