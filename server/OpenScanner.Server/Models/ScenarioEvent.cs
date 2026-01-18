using System.Text.Json.Serialization;

namespace OpenScanner.Server.Models;

public class ScenarioEvent
{
    [JsonPropertyName("time")]
    public double Time { get; set; }

    [JsonPropertyName("frequency")]
    public double Frequency { get; set; }

    [JsonPropertyName("audio_file")]
    public string? AudioFile { get; set; }

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("source_id")]
    public int? SourceId { get; set; }

    [JsonPropertyName("target_id")]
    public int? TargetId { get; set; }

    [JsonPropertyName("decoder_type")]
    public string? DecoderType { get; set; }
}
