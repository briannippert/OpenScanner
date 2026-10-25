namespace OpenScanner.Server.Audio;

/// <summary>
/// What a given <c>/ws/audio</c> session is being sent.
/// </summary>
public enum LiveAudioCodec
{
    /// <summary>
    /// No <c>?codec=</c> parameter at all. The client is from before codec negotiation existed, so
    /// it gets exactly what it always got: raw s16le PCM binary frames and no text frames.
    /// </summary>
    LegacyPcm,

    /// <summary>Raw s16le PCM, but with <c>AUDIO_FORMAT</c> announcements.</summary>
    Pcm,

    /// <summary>One Opus packet per binary frame, with <c>AUDIO_FORMAT</c> announcements.</summary>
    Opus,
}

/// <summary>
/// Resolves the <c>?codec=</c> query parameter on the audio WebSocket.
/// </summary>
public static class AudioNegotiation
{
    /// <summary>
    /// Picks a codec from the client's ordered preference list.
    /// </summary>
    /// <param name="codecParam">Raw <c>?codec=</c> value (comma-separated, most preferred first),
    /// or null when the parameter is absent.</param>
    /// <param name="opusAvailable">False when the server can't encode Opus, which downgrades an
    /// Opus request to PCM rather than failing the connection.</param>
    public static LiveAudioCodec Negotiate(string? codecParam, bool opusAvailable)
    {
        if (codecParam is null) return LiveAudioCodec.LegacyPcm;

        foreach (var raw in codecParam.Split(','))
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "opus" when opusAvailable:
                    return LiveAudioCodec.Opus;
                case "pcm":
                case "pcm_s16le":
                    return LiveAudioCodec.Pcm;
            }
        }

        // The parameter was present but named nothing we support (including "opus" when Opus is
        // unavailable). The client still speaks the negotiated protocol, so it gets AUDIO_FORMAT.
        return LiveAudioCodec.Pcm;
    }
}
