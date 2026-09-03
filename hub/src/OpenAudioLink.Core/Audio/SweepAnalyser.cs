namespace OpenAudioLink.Core.Audio;

/// <summary>Knobs on the analysis. The defaults are the measurement.</summary>
public sealed record SweepAnalysisOptions
{
    /// <summary>
    /// How long an impulse response is taken to last. Half a second holds
    /// a domestic room's decay; making it longer only admits noise, and
    /// making it much shorter throws away the room modes this exists to
    /// find.
    /// </summary>
    public double WindowSeconds { get; init; } = 0.5;

    /// <summary>
    /// Kept before the peak, so the window does not begin on a step. Also
    /// where the sweep's harmonic distortion lands: with an exponential
    /// sweep the nth harmonic arrives a fixed time <i>ahead</i> of its
    /// fundamental, so a short pre-window is what separates the
    /// loudspeaker's distortion from its response.
    /// </summary>
    public double PreWindowSeconds { get; init; } = 0.005;

    /// <summary>
    /// Fraction of an octave averaged into each reported point. A raw
    /// response is a forest of interference nulls that move when the
    /// microphone moves an inch; a sixth of an octave keeps what is a
    /// property of the room and loses what is a property of the tripod.
    /// </summary>
    public double SmoothingOctave { get; init; } = 1.0 / 6.0;

    /// <summary>
    /// How far before the sweep's arrival the analysis window starts, so
    /// the impulse response has somewhere positive to sit. Slack against
    /// the coarse alignment, not a tuning parameter.
    /// </summary>
    public double AlignMarginSeconds { get; init; } = 0.25;

    /// <summary>
    /// Added to the sweep's own power before dividing by it, as a fraction
    /// of the sweep's peak power. Outside the swept band the sweep has no
    /// energy, and dividing by that would report the microphone's noise
    /// amplified by a hundred decibels as if it were a room.
    /// </summary>
    public double Regularisation { get; init; } = 1e-8;

    public double LowHz { get; init; } = 20.0;

    public double HighHz { get; init; } = 20000.0;

    /// <summary>Points on the reported curve, spaced logarithmically.</summary>
    public int Points { get; init; } = 240;

    /// <summary>
    /// The band the curve is levelled against, so 0 dB means "as loud as
    /// the middle". Absolute level would need a calibrated microphone,
    /// which this is not.
    /// </summary>
    public double ReferenceLowHz { get; init; } = 200.0;

    public double ReferenceHighHz { get; init; } = 2000.0;
}

/// <summary>What a room did to a sweep.</summary>
public sealed record RoomResponse
{
    public required IReadOnlyList<double> FrequenciesHz { get; init; }

    /// <summary>Level at each frequency, in dB relative to the middle band.</summary>
    public required IReadOnlyList<double> MagnitudeDb { get; init; }

    /// <summary>The windowed impulse response, for anything fitted to it later.</summary>
    public required IReadOnlyList<double> ImpulseResponse { get; init; }

    public required int SampleRate { get; init; }

    /// <summary>
    /// How many whole sweeps were averaged. Each one is an independent
    /// look at the same room, so this is the measurement's confidence:
    /// noise falls as its square root.
    /// </summary>
    public required int CyclesAveraged { get; init; }

    /// <summary>
    /// Sweep energy over the noise in the quiet part of the cycle. Below
    /// about 20 dB the bottom of the curve is the room's noise floor
    /// rather than the room.
    /// </summary>
    public required double SignalToNoiseDb { get; init; }

    /// <summary>Peak of the recording, so a clipped measurement is visible.</summary>
    public required double PeakDbFs { get; init; }

    /// <summary>
    /// Samples that reached full scale. Anything but zero makes the curve
    /// a measurement of the clipping.
    /// </summary>
    public required long ClippedSamples { get; init; }

    /// <summary>
    /// Where the direct sound landed in the analysis buffer, measured from
    /// where the fold began. It should come out close to the alignment
    /// margin; far from it means the coarse alignment found the wrong
    /// thing, and the curve should not be trusted.
    /// </summary>
    /// <remarks>
    /// Not a latency figure. The window is placed by looking for the
    /// arrival, so the network and playout delay has already been taken
    /// out, and the three clocks involved are not the same one.
    /// </remarks>
    public required double ImpulsePeakSeconds { get; init; }

    /// <summary>Where the fold began relative to the detected arrival.</summary>
    public required double AlignMarginSeconds { get; init; }

    /// <summary>Anything a reader needs to know before trusting the curve.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Turns a recording of <see cref="SweepSignal"/> into a frequency response.
/// </summary>
/// <remarks>
/// <para>
/// The method is division: what came back, divided by what went out, is
/// what the room did. Everything else here is the bookkeeping that makes
/// that division legitimate.
/// </para>
/// <para>
/// <b>Averaging.</b> The signal repeats every cycle, so the recording is
/// folded on the cycle and averaged. Noise is uncorrelated between cycles
/// and falls as the square root of their number; the sweep does not.
/// </para>
/// <para>
/// <b>Alignment.</b> The recording starts whenever somebody pressed the
/// button. Folding at the wrong phase would rotate the response rather than
/// delay it, and a rotation is not something a division can undo — so the
/// silent part of the cycle is found first, by the direct method of looking
/// for where the energy is not.
/// </para>
/// <para>
/// <b>Why the padding is exact.</b> Dividing spectra is a circular
/// operation, and circular arithmetic wraps a room's decay back onto the
/// start of the sweep. It does not here, and the silent gap is why: the
/// response is shorter than the gap, so the periodic response and the
/// linear one are the same sequence, and zero-padding to a power of two for
/// the transform changes nothing. That is the gap's real job.
/// </para>
/// </remarks>
public static class SweepAnalyser
{
    /// <exception cref="ArgumentException">
    /// The recording is too short to contain a whole sweep. Said rather
    /// than answered with a curve, because a curve from half a sweep looks
    /// like a curve.
    /// </exception>
    public static RoomResponse Analyse(
        ReadOnlySpan<double> recording, SweepSignal signal, SweepAnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(signal);
        signal.Validate();
        options ??= new SweepAnalysisOptions();

        int rate = signal.SampleRate;
        int cycle = signal.CycleFrames;
        int sweep = signal.SweepFrames;

        if (recording.Length < 2 * cycle)
        {
            throw new ArgumentException(
                $"A measurement needs at least two whole cycles — "
                + $"{2.0 * cycle / rate:0.#} s — and this recording is "
                + $"{(double)recording.Length / rate:0.#} s.", nameof(recording));
        }

        var warnings = new List<string>();

        double peak = 0;
        long clipped = 0;
        foreach (double sample in recording)
        {
            double magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
            if (magnitude >= 0.999)
            {
                clipped++;
            }
        }
        if (clipped > 0)
        {
            warnings.Add(
                $"{clipped} sample(s) reached full scale — the curve includes the clipping. "
                + "Lower the microphone gain or the speaker and measure again.");
        }

        int arrival = FindSweepArrival(recording, cycle, sweep);
        // Never more than a quarter of the gap: the margin buys slack in
        // front of the response, and the rest of the gap is what the
        // response itself is allowed to occupy before it wraps.
        int margin = Math.Clamp(
            (int)Math.Round(options.AlignMarginSeconds * rate), 0, Math.Max(1, (cycle - sweep) / 4));
        int start = ((arrival - margin) % cycle + cycle) % cycle;

        // The first cycle is skipped: the receiver is still filling its
        // buffer, and a sweep half of which was sent before the recorder
        // opened its socket would be averaged in as a quieter one.
        int first = start + cycle;
        int cycles = (recording.Length - first) / cycle;
        if (cycles < 1)
        {
            first = start;
            cycles = (recording.Length - first) / cycle;
            warnings.Add("Only one whole sweep was recorded; there was nothing to average.");
        }

        var folded = new double[cycle];
        for (int k = 0; k < cycles; k++)
        {
            int at = first + k * cycle;
            for (int n = 0; n < cycle; n++)
            {
                folded[n] += recording[at + n];
            }
        }
        for (int n = 0; n < cycle; n++)
        {
            folded[n] /= cycles;
        }

        double snr = SignalToNoise(folded, margin, sweep, rate);
        if (snr < 20)
        {
            warnings.Add(
                $"Only {snr:0} dB of sweep above the room's noise. The quiet end of the curve "
                + "is noise; play it louder, record for longer, or measure in a quieter room.");
        }

        int size = Fft.NextPowerOfTwo(cycle);

        var referenceRe = new double[size];
        var referenceIm = new double[size];
        for (int n = 0; n < cycle; n++)
        {
            referenceRe[n] = signal.SampleAt(n);
        }
        Fft.Forward(referenceRe, referenceIm);

        var measuredRe = new double[size];
        var measuredIm = new double[size];
        folded.CopyTo(measuredRe, 0);
        Fft.Forward(measuredRe, measuredIm);

        /*
         * H = Y * conj(X) / (|X|^2 + e), which is the division with a floor
         * under it. Outside the swept band |X| is nothing at all, and the
         * unregularised quotient there is the microphone's own noise
         * multiplied by an arbitrarily large number — a spectacular curve
         * describing nothing. With the floor it goes quietly to zero, which
         * is the honest answer for a band that was never excited.
         */
        double strongest = 0;
        for (int k = 0; k < size; k++)
        {
            double power = referenceRe[k] * referenceRe[k] + referenceIm[k] * referenceIm[k];
            if (power > strongest)
            {
                strongest = power;
            }
        }
        double floor = strongest * options.Regularisation;

        var responseRe = new double[size];
        var responseIm = new double[size];
        for (int k = 0; k < size; k++)
        {
            double power = referenceRe[k] * referenceRe[k] + referenceIm[k] * referenceIm[k] + floor;
            responseRe[k] = (measuredRe[k] * referenceRe[k] + measuredIm[k] * referenceIm[k]) / power;
            responseIm[k] = (measuredIm[k] * referenceRe[k] - measuredRe[k] * referenceIm[k]) / power;
        }

        Fft.Inverse(responseRe, responseIm);

        int peakAt = 0;
        double peakLevel = 0;
        for (int n = 0; n < size; n++)
        {
            double magnitude = Math.Abs(responseRe[n]);
            if (magnitude > peakLevel)
            {
                peakLevel = magnitude;
                peakAt = n;
            }
        }

        /*
         * The peak should land on the margin: the fold starts that far
         * before the arrival, so that is where the direct sound belongs.
         * Somewhere else means the alignment locked onto the wrong edge —
         * a room with a long decay, a recording of something that is not
         * this sweep — and every number after this point is then a
         * measurement of whatever it did find.
         */
        int drift = Math.Abs(peakAt - margin);
        if (drift > (cycle - sweep) / 2)
        {
            warnings.Add(
                $"The direct sound landed {(double)peakAt / rate:0.00} s into the window rather than "
                + $"near {(double)margin / rate:0.00} s. The alignment did not find the sweep, and the "
                + "curve is not a measurement of this room.");
        }

        var impulse = Window(responseRe, peakAt, rate, options);

        var windowedRe = new double[size];
        var windowedIm = new double[size];
        impulse.CopyTo(windowedRe, 0);
        Fft.Forward(windowedRe, windowedIm);

        var (frequencies, magnitudes) = Curve(windowedRe, windowedIm, rate, size, options);
        Level(frequencies, magnitudes, options);

        return new RoomResponse
        {
            FrequenciesHz = frequencies,
            MagnitudeDb = magnitudes,
            ImpulseResponse = impulse,
            SampleRate = rate,
            CyclesAveraged = cycles,
            SignalToNoiseDb = snr,
            PeakDbFs = peak <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(peak),
            ClippedSamples = clipped,
            ImpulsePeakSeconds = (double)peakAt / rate,
            AlignMarginSeconds = (double)margin / rate,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// The phase within the cycle at which the sweep arrives, found by
    /// looking for the quiet part rather than for the loud one.
    /// </summary>
    /// <remarks>
    /// Correlating against the sweep would seem more direct and is worse:
    /// a logarithmic sweep's autocorrelation is broad and dominated by its
    /// bottom octave, so the peak is soft and moves with the room. The
    /// silence has an edge. Power is folded onto one cycle first, so every
    /// repetition votes, and a prefix sum makes the search over every
    /// possible phase a single pass.
    /// </remarks>
    private static int FindSweepArrival(ReadOnlySpan<double> recording, int cycle, int sweep)
    {
        var power = new double[cycle];
        int cycles = recording.Length / cycle;
        for (int k = 0; k < cycles; k++)
        {
            int at = k * cycle;
            for (int n = 0; n < cycle; n++)
            {
                power[n] += recording[at + n] * recording[at + n];
            }
        }

        var prefix = new double[cycle + 1];
        for (int n = 0; n < cycle; n++)
        {
            prefix[n + 1] = prefix[n] + power[n];
        }
        double total = prefix[cycle];

        int quietest = 0;
        double least = double.MaxValue;
        for (int offset = 0; offset < cycle; offset++)
        {
            // Energy of [offset + sweep, offset + cycle) taken circularly,
            // which is the same as everything except [offset, offset + sweep).
            int from = offset;
            int to = offset + sweep;
            double loud = to <= cycle
                ? prefix[to] - prefix[from]
                : prefix[cycle] - prefix[from] + prefix[to - cycle];

            double quiet = total - loud;
            if (quiet < least)
            {
                least = quiet;
                quietest = offset;
            }
        }
        return quietest;
    }

    /// <summary>
    /// The sweep's power against the power in the last part of the gap,
    /// which is the only stretch of the cycle holding neither the sweep nor
    /// its reverberation.
    /// </summary>
    private static double SignalToNoise(double[] folded, int margin, int sweep, int rate)
    {
        int cycle = folded.Length;
        int noiseFrom = Math.Max(margin + sweep, cycle - Math.Min(rate / 2, cycle - margin - sweep));
        if (noiseFrom >= cycle)
        {
            return double.NaN;
        }

        double signal = 0;
        for (int n = margin; n < Math.Min(margin + sweep, cycle); n++)
        {
            signal += folded[n] * folded[n];
        }
        signal /= Math.Min(sweep, cycle - margin);

        double noise = 0;
        for (int n = noiseFrom; n < cycle; n++)
        {
            noise += folded[n] * folded[n];
        }
        noise /= cycle - noiseFrom;

        if (noise <= 0)
        {
            return double.PositiveInfinity;
        }
        return 10.0 * Math.Log10(signal / noise);
    }

    /// <summary>
    /// Cuts the impulse response out of the deconvolved buffer and tapers
    /// both ends of it.
    /// </summary>
    private static double[] Window(double[] response, int peakAt, int rate, SweepAnalysisOptions options)
    {
        int size = response.Length;
        int pre = Math.Min((int)Math.Round(options.PreWindowSeconds * rate), peakAt);
        int length = Math.Min((int)Math.Round(options.WindowSeconds * rate), size);
        if (length <= pre + 1)
        {
            length = Math.Min(pre + 2, size);
        }

        var windowed = new double[length];
        int decayFrom = length - length / 4;

        for (int i = 0; i < length; i++)
        {
            double taper = 1.0;
            if (pre > 0 && i < pre)
            {
                // Rise into the pre-window, so the cut before the direct
                // sound is a fade rather than a step. A step here is a
                // wideband click that would be added to the answer.
                taper = 0.5 * (1.0 - Math.Cos(Math.PI * i / pre));
            }
            else if (i >= decayFrom)
            {
                double into = (double)(i - decayFrom) / (length - decayFrom);
                taper = 0.5 * (1.0 + Math.Cos(Math.PI * into));
            }

            int at = ((peakAt - pre + i) % size + size) % size;
            windowed[i] = response[at] * taper;
        }
        return windowed;
    }

    /// <summary>
    /// The spectrum reduced to a logarithmic grid, each point the average
    /// power of a fractional-octave band around it.
    /// </summary>
    private static (double[] Frequencies, double[] Magnitudes) Curve(
        double[] re, double[] im, int rate, int size, SweepAnalysisOptions options)
    {
        int points = Math.Max(2, options.Points);
        var frequencies = new double[points];
        var magnitudes = new double[points];

        double perBin = (double)rate / size;
        double half = Math.Pow(2.0, options.SmoothingOctave / 2.0);
        double ratio = Math.Log(options.HighHz / options.LowHz);

        for (int p = 0; p < points; p++)
        {
            double frequency = options.LowHz * Math.Exp(ratio * p / (points - 1));
            frequencies[p] = frequency;

            int from = (int)Math.Floor(frequency / half / perBin);
            int to = (int)Math.Ceiling(frequency * half / perBin);
            from = Math.Clamp(from, 1, size / 2 - 1);
            to = Math.Clamp(to, from, size / 2 - 1);

            double power = 0;
            for (int k = from; k <= to; k++)
            {
                power += re[k] * re[k] + im[k] * im[k];
            }
            power /= to - from + 1;

            magnitudes[p] = power <= 0 ? -200.0 : 10.0 * Math.Log10(power);
        }

        return (frequencies, magnitudes);
    }

    /// <summary>
    /// Slides the curve so that the middle band sits at 0 dB. What is
    /// reported is the shape; the absolute level would be a property of the
    /// microphone's sensitivity, which is not calibrated.
    /// </summary>
    private static void Level(double[] frequencies, double[] magnitudes, SweepAnalysisOptions options)
    {
        double sum = 0;
        int count = 0;
        for (int p = 0; p < frequencies.Length; p++)
        {
            if (frequencies[p] >= options.ReferenceLowHz && frequencies[p] <= options.ReferenceHighHz)
            {
                sum += magnitudes[p];
                count++;
            }
        }
        if (count == 0)
        {
            return;
        }

        double reference = sum / count;
        for (int p = 0; p < magnitudes.Length; p++)
        {
            magnitudes[p] -= reference;
        }
    }
}
