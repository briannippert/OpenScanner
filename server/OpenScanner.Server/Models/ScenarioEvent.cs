namespace OpenScanner.Server.Models;

public class ScenarioEvent
{
    public double Time { get; set; }
    public double Frequency { get; set; }
    public string? AudioFile { get; set; }
    public double Duration { get; set; }
    public int? SourceId { get; set; }
    public int? TargetId { get; set; }
    public string? DecoderType { get; set; }
}
