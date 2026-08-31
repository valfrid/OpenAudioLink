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

    /// <summary>
    /// Samples decoded and waiting to be sent, and how many the source
    /// wants waiting before it considers itself charged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both zero for a source that has no cushion to fill — a tone is
    /// generated on demand, and a capture device is as fast as the room.
    /// Only a source fetching from somewhere slower than real time has a
    /// charge worth waiting for.
    /// </para>
    /// <para>
    /// Reported rather than assumed, so anything showing progress shows
    /// the real fill. A fixed countdown started at the press of a button
    /// would keep counting while a station failed to open, and reach zero
    /// having proved nothing.
    /// </para>
    /// </remarks>
    long BufferedSamples => 0;

    /// <inheritdoc cref="BufferedSamples"/>
    long TargetBufferedSamples => 0;
}
