namespace OpenAudioLink.Core.Audio;

/// <summary>
/// Generates a continuous sine tone as interleaved samples in [-1, 1].
///
/// This is the reference signal for bringing up the audio path without
/// audio hardware: a 1 kHz tone is easy to recognise by ear, and its
/// shape makes byte-order and channel-order mistakes obvious on a scope
/// or spectrum view (see docs/HARDWARE.md).
/// </summary>
public sealed class SineToneSource : IAudioSource
{
    private readonly double _radiansPerFrame;
    private readonly int _channels;
    private readonly float _amplitude;
    private double _phase;

    public SineToneSource(AudioStreamFormat format, double frequencyHz = 1000.0, float amplitude = 0.5f)
    {
        ArgumentNullException.ThrowIfNull(format);

        if (frequencyHz <= 0 || frequencyHz >= format.SampleRate / 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz),
                "Frequency must be above zero and below the Nyquist limit.");
        }

        _radiansPerFrame = 2.0 * Math.PI * frequencyHz / format.SampleRate;
        _channels = format.Channels;
        _amplitude = amplitude;
        Description = $"{frequencyHz:0.#} Hz test tone";
    }

    public string Description { get; }

    public void Dispose()
    {
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with interleaved samples. The
    /// same value is written to every channel, and the phase continues
    /// across calls so consecutive packets join without a click.
    /// </summary>
    public void ReadFrames(Span<float> destination)
    {
        if (destination.Length % _channels != 0)
        {
            throw new ArgumentException(
                $"Destination length must be a multiple of {_channels} channels.", nameof(destination));
        }

        for (int i = 0; i < destination.Length; i += _channels)
        {
            float value = _amplitude * MathF.Sin((float)_phase);
            for (int channel = 0; channel < _channels; channel++)
            {
                destination[i + channel] = value;
            }

            _phase += _radiansPerFrame;
            if (_phase >= 2.0 * Math.PI)
            {
                _phase -= 2.0 * Math.PI;
            }
        }
    }
}
