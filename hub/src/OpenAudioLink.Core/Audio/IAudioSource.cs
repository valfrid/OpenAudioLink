namespace OpenAudioLink.Core.Audio;

/// <summary>
/// A source of audio in the stream's reference format, pulled by the
/// sender one packet at a time.
///
/// Implementations must always fill the buffer: a source with nothing to
/// give writes silence rather than blocking or returning short, so the
/// stream keeps its timing and receivers keep their jitter buffers fed.
/// </summary>
public interface IAudioSource : IDisposable
{
    /// <summary>Human-readable description for status and diagnostics.</summary>
    string Description { get; }

    /// <summary>
    /// Fills <paramref name="destination"/> with interleaved samples in
    /// the range [-1, 1]. Length is always a whole number of frames.
    /// </summary>
    void ReadFrames(Span<float> destination);

    /// <summary>
    /// Samples of silence substituted because the source could not keep
    /// up. Zero for sources that generate audio on demand.
    /// </summary>
    long UnderrunSamples => 0;
}
