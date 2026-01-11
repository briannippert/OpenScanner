namespace OpenScanner.Server.Interfaces;

public interface IDecoderFactory
{
    IDecoder GetDecoder(string? mode);
}