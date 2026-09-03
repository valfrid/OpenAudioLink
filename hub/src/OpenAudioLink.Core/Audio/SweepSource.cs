using OpenAudioLink.Core.Devices;

namespace OpenAudioLink.Core.Audio;

/// <summary>
/// Plays <see cref="SweepSignal"/> down the stream, on one channel or both.
/// </summary>
/// <remarks>
/// <para>
/// The channel is the measurement's most important control and the easiest
/// to overlook. Two speakers playing the same sweep are two sources
/// arriving at the microphone at different times, and their sum has deep
/// cancellations that belong to the pair rather than to either speaker —
/// correcting for them would make each speaker worse. A room is measured
/// one loudspeaker at a time.
/// </para>
/// <para>
/// Which is why this reuses the stream's own channels rather than inventing
/// a target: every node already knows which half of the stream it plays
/// (decision 10), so putting the sweep on the left channel silences the
/// right-hand speaker without anything being told to stop.
/// </para>
/// </remarks>
public sealed class SweepSource : IAudioSource
{
    private readonly SweepSignal _signal;
    private readonly int _channels;
    private readonly bool _left;
    private readonly bool _right;
    private long _frame;

    /// <param name="channel">
    /// One of <see cref="AudioChannel"/>. <c>stereo</c> and <c>mono</c> put
    /// the sweep on both channels; <c>left</c> and <c>right</c> put silence
    /// on the other one.
    /// </param>
    public SweepSource(AudioStreamFormat format, SweepSignal? signal = null, string? channel = null)
    {
        ArgumentNullException.ThrowIfNull(format);

        _signal = signal ?? new SweepSignal { SampleRate = format.SampleRate };
        if (_signal.SampleRate != format.SampleRate)
        {
            throw new ArgumentException(
                $"The sweep is defined at {_signal.SampleRate} Hz but the stream runs at "
                + $"{format.SampleRate} Hz; resampling it would move every frequency in the answer.",
                nameof(signal));
        }
        _signal.Validate();

        channel ??= AudioChannel.Stereo;
        if (!AudioChannels.IsKnown(channel))
        {
            throw new ArgumentException($"'{channel}' is not a channel.", nameof(channel));
        }

        _channels = format.Channels;
        _left = channel != AudioChannel.Right;
        _right = channel != AudioChannel.Left;
        Channel = channel;

        Description = channel switch
        {
            AudioChannel.Left => $"{_signal} (left speaker only)",
            AudioChannel.Right => $"{_signal} (right speaker only)",
            _ => $"{_signal} (both channels)",
        };
    }

    public string Description { get; }

    /// <summary>Which channel carries it, for the recording's notes.</summary>
    public string Channel { get; }

    public SweepSignal Signal => _signal;

    /// <summary>
    /// Frames emitted since the source was created, which is also the
    /// position in the signal — the analyser folds on
    /// <see cref="SweepSignal.CycleFrames"/>, so this says how many whole
    /// looks at the room have been sent.
    /// </summary>
    public long FramesEmitted => Interlocked.Read(ref _frame);

    /// <summary>Complete sweeps sent so far.</summary>
    public long CyclesEmitted => FramesEmitted / _signal.CycleFrames;

    public void Dispose()
    {
    }

    public void ReadFrames(Span<float> destination)
    {
        if (destination.Length % _channels != 0)
        {
            throw new ArgumentException(
                $"Destination length must be a multiple of {_channels} channels.", nameof(destination));
        }

        long frame = _frame;
        for (int i = 0; i < destination.Length; i += _channels)
        {
            float value = (float)_signal.SampleAt(frame);
            for (int channel = 0; channel < _channels; channel++)
            {
                // Channels beyond the second are not part of this profile;
                // give them the signal rather than silence so a wider
                // format is quiet in no unexpected place.
                bool carries = channel switch
                {
                    0 => _left,
                    1 => _right,
                    _ => true,
                };
                destination[i + channel] = carries ? value : 0f;
            }
            frame++;
        }

        Interlocked.Exchange(ref _frame, frame);
    }
}
