using OpenScanner.Server.Models;

namespace OpenScanner.Server.Interfaces;

/// <summary>
/// Supplies the audio for a <see cref="ScenarioEvent"/> played back by
/// <c>MockRadioSource</c>. Abstracting this out lets the running app stream
/// real recorded audio (files / ffmpeg) while tests inject deterministic,
/// dependency-free synthetic audio.
/// </summary>
public interface IMockAudioProvider
{
    /// <summary>
    /// Streams the audio for <paramref name="evt"/>, invoking
    /// <paramref name="onChunk"/> for each PCM chunk (48 kHz s16le mono).
    /// Implementations may pace playback in real time or emit synchronously.
    /// </summary>
    Task StreamAsync(ScenarioEvent evt, Action<byte[]> onChunk, CancellationToken token);
}
