namespace OpenAudioLink.Core.Audio;

/// <summary>
/// The measurement signal: a logarithmic sine sweep followed by silence,
/// repeated forever.
/// </summary>
/// <remarks>
/// <para>
/// This is one half of a room measurement; the recorder is the other. A
/// speaker plays this, a microphone at the listening position records it,
/// and dividing what came back by what went out gives the room's frequency
/// response. Everything downstream — the curve, and the correction
/// constants derived from it — depends on the analyser being able to
/// reproduce this signal exactly, so the definition lives in one place and
/// both ends compute from it rather than from a recording of it.
/// </para>
/// <para>
/// <b>Why a sweep and not noise.</b> A sweep puts all its energy at one
/// frequency at a time, so it reaches a useful signal-to-noise ratio at
/// every frequency with a loudspeaker that is not being driven hard. It
/// also separates the loudspeaker's harmonic distortion from its linear
/// response: because the frequency rises exponentially, a harmonic arrives
/// at a fixed time <i>ahead</i> of the fundamental, so distortion lands
/// before the impulse response and a window throws it away. Noise gives
/// neither property.
/// </para>
/// <para>
/// <b>Why logarithmic and not linear.</b> A linear sweep spends nine tenths
/// of its time above 2 kHz. Rooms misbehave in the bottom two octaves, and
/// that is where a measurement needs its time.
/// </para>
/// <para>
/// <b>Why the silence matters.</b> The gap is not padding. It has to be
/// longer than the room's reverberation, or the tail of one sweep overlaps
/// the head of the next and the analyser folds late reflections back onto
/// the direct sound. Two seconds covers a domestic room several times over
/// (an ordinary living room decays in 0.3–0.6 s).
/// </para>
/// <para>
/// <b>Why it repeats.</b> Every complete cycle is an independent look at
/// the same room. Averaging them lifts the signal out of the noise by the
/// square root of their number, which is what makes a −91 dBFS microphone
/// in a −63 dBFS room a usable instrument.
/// </para>
/// <para>
/// The signal is a pure function of the frame index — the same rule the
/// firmware's pattern source follows — so what a packet contains depends on
/// where it sits in the stream, never on when it was generated. A sender
/// restarted mid-measurement resumes the same waveform.
/// </para>
/// </remarks>
public sealed record SweepSignal
{
    /// <summary>
    /// Where the sweep begins. Below this the loudspeaker contributes
    /// mostly excursion and the ICS-43434 is out of its specified band
    /// (50 Hz), but starting at 20 Hz costs half a second and shows where
    /// the roll-off actually is instead of assuming it.
    /// </summary>
    public double StartHz { get; init; } = 20.0;

    /// <summary>The top of the audio band, and just under Nyquist at 48 kHz.</summary>
    public double EndHz { get; init; } = 20000.0;

    /// <summary>
    /// How long the sweep itself lasts. Longer is quieter for the same
    /// signal-to-noise ratio; eight seconds is enough that the bottom
    /// octave gets about a second of excitation.
    /// </summary>
    public double SweepSeconds { get; init; } = 8.0;

    /// <summary>Silence after the sweep, for the room to stop ringing.</summary>
    public double SilenceSeconds { get; init; } = 2.0;

    /// <summary>
    /// Peak amplitude, −6 dBFS. Headroom matters more here than level: a
    /// sweep that clips is a measurement of the clipping.
    /// </summary>
    public double Amplitude { get; init; } = 0.5;

    public int SampleRate { get; init; } = 48000;

    /// <summary>
    /// A raised cosine at the start, over five cycles of
    /// <see cref="StartHz"/>. Switching a 20 Hz sine on at full amplitude
    /// is a step, and a step excites everything — which would show up as a
    /// second, untimed impulse in the result.
    /// </summary>
    public double FadeInSeconds { get; init; } = 0.25;

    /// <summary>
    /// Shorter than the fade in, because the end of the sweep is at 20 kHz
    /// where ten milliseconds is two hundred cycles.
    /// </summary>
    public double FadeOutSeconds { get; init; } = 0.01;

    /// <summary>Frames of sweep. The authority; the seconds are the request.</summary>
    public int SweepFrames => Frames(SweepSeconds);

    /// <summary>Frames of silence.</summary>
    public int SilenceFrames => Frames(SilenceSeconds);

    /// <summary>Sweep plus silence: the period the analyser folds on.</summary>
    public int CycleFrames => SweepFrames + SilenceFrames;

    public TimeSpan CycleDuration => TimeSpan.FromSeconds((double)CycleFrames / SampleRate);

    private int Frames(double seconds) => (int)Math.Round(seconds * SampleRate);

    /// <summary>
    /// Refuses a signal that cannot be measured with, rather than producing
    /// one that quietly is not a sweep.
    /// </summary>
    public void Validate()
    {
        if (SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleRate), "Sample rate must be positive.");
        }
        if (StartHz <= 0 || EndHz <= StartHz)
        {
            throw new ArgumentOutOfRangeException(nameof(StartHz),
                "The sweep must rise: 0 < StartHz < EndHz.");
        }
        if (EndHz > SampleRate / 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(EndHz),
                $"{EndHz:0} Hz is above the {SampleRate / 2.0:0} Hz Nyquist limit; it would alias.");
        }
        if (SweepFrames <= 0 || SilenceFrames < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SweepSeconds),
                "The sweep must last at least one frame and the silence cannot be negative.");
        }
        if (FadeInSeconds < 0 || FadeOutSeconds < 0
            || Frames(FadeInSeconds) + Frames(FadeOutSeconds) > SweepFrames)
        {
            throw new ArgumentOutOfRangeException(nameof(FadeInSeconds),
                "The fades do not fit inside the sweep.");
        }
        if (Amplitude <= 0 || Amplitude > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(Amplitude),
                "Amplitude is a fraction of full scale, above zero and at most one.");
        }
    }

    /// <summary>
    /// The sample at an absolute frame index, wrapping at the cycle. Both
    /// the sender and the analyser call this, and neither has any other
    /// definition of the signal to disagree with.
    /// </summary>
    public double SampleAt(long frame)
    {
        int cycle = CycleFrames;
        long n = frame % cycle;
        if (n < 0)
        {
            n += cycle;
        }
        if (n >= SweepFrames)
        {
            return 0.0;
        }

        /*
         * f(t) = f1 * (f2/f1)^(t/T), whose integral is the phase:
         *
         *     phi(t) = 2*pi * f1 * T / L * (e^(t*L/T) - 1),   L = ln(f2/f1)
         *
         * The whole shape of the measurement follows from that exponent:
         * every octave gets the same number of seconds, so the bottom two
         * — the ones a room ruins — get a quarter of the sweep between
         * them rather than the 0.2 % a linear sweep would give them.
         *
         * T is taken from the frame count, not from SweepSeconds, so the
         * signal is exactly periodic in CycleFrames. A fraction of a frame
         * of drift per cycle would smear every average the analyser takes.
         */
        double seconds = (double)SweepFrames / SampleRate;
        double l = Math.Log(EndHz / StartHz);
        double t = (double)n / SampleRate;
        double phase = 2.0 * Math.PI * StartHz * seconds / l * (Math.Exp(t * l / seconds) - 1.0);

        return Amplitude * Math.Sin(phase) * Envelope(n);
    }

    /// <summary>
    /// The raised-cosine fades at each end of the sweep, as a factor in
    /// [0, 1]. Flat everywhere between them.
    /// </summary>
    public double Envelope(long n)
    {
        if (n < 0 || n >= SweepFrames)
        {
            return 0.0;
        }

        int fadeIn = Frames(FadeInSeconds);
        if (fadeIn > 0 && n < fadeIn)
        {
            return 0.5 * (1.0 - Math.Cos(Math.PI * n / fadeIn));
        }

        int fadeOut = Frames(FadeOutSeconds);
        long remaining = SweepFrames - n;
        if (fadeOut > 0 && remaining <= fadeOut)
        {
            return 0.5 * (1.0 - Math.Cos(Math.PI * remaining / fadeOut));
        }

        return 1.0;
    }

    /// <summary>
    /// Writes one whole cycle, which is what the analyser divides by.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The destination is not exactly a cycle. A short buffer here would
    /// be a reference signal that is not the one that was played, and the
    /// error would appear as a tilt in the answer rather than as a fault.
    /// </exception>
    public void RenderCycle(Span<double> destination)
    {
        if (destination.Length != CycleFrames)
        {
            throw new ArgumentException(
                $"A cycle is {CycleFrames} frames, not {destination.Length}.", nameof(destination));
        }

        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = SampleAt(i);
        }
    }

    public override string ToString() =>
        $"{StartHz:0.#} Hz–{EndHz / 1000:0.#} kHz sweep, "
        + $"{(double)SweepFrames / SampleRate:0.#} s + {(double)SilenceFrames / SampleRate:0.#} s silence";
}
