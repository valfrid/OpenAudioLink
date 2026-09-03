namespace OpenAudioLink.Core.Audio;

/// <summary>
/// The rules a correction is fitted under. Every one of them exists to stop
/// the correction doing more harm than the room does.
/// </summary>
public sealed record CorrectionRules
{
    /// <summary>
    /// The bottom of the band that may be corrected. Below this the
    /// measurement is the room's noise floor rather than the room — the
    /// sweep is weakest there and the microphone is outside its specified
    /// range.
    /// </summary>
    public double LowHz { get; init; } = 30.0;

    /// <summary>
    /// The top of it, and the most important number here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Above roughly 300 Hz a room stops behaving like a few resonances and
    /// starts behaving like a dense field of reflections. What is measured
    /// there is a property of where the microphone stood, not of the room:
    /// move it a hand's width and the interference pattern is different.
    /// Correcting it makes that one spot flatter and every other spot
    /// worse.
    /// </para>
    /// <para>
    /// It is also where this system stops being able to tell the room from
    /// the instrument. Both loudspeakers measured here slope down by 15 dB
    /// between 2 and 10 kHz and rise again above 12 — the same shape on
    /// both, which is the signature of the microphone rather than of either
    /// speaker. Correcting that would cut treble that was never loud.
    /// </para>
    /// </remarks>
    public double HighHz { get; init; } = 300.0;

    /// <summary>
    /// The most a correction may boost. Boost costs amplifier power and
    /// excursion, and a dip that needs more than this is usually a
    /// cancellation that no amount of power fills.
    /// </summary>
    public double MaxBoostDb { get; init; } = 3.0;

    /// <summary>
    /// The most it may cut. Cutting is nearly free and a room's peaks are
    /// what people actually hear as boom, so this is the generous
    /// direction.
    /// </summary>
    public double MaxCutDb { get; init; } = 9.0;

    /// <summary>
    /// Dips are corrected at this fraction of the amount peaks are.
    /// "Correct broad peaks strongly, broad dips moderately": a peak is
    /// energy that is there and can be removed, while a dip is energy that
    /// cancelled and mostly cannot be put back.
    /// </summary>
    public double DipRestraint { get; init; } = 0.5;

    /// <summary>
    /// Nothing narrower than this is touched. A narrow null moves when the
    /// microphone moves, so a filter fitted to one is a filter fitted to
    /// the tripod.
    /// </summary>
    public double MaxQ { get; init; } = 8.0;

    /// <summary>Nothing shallower than this is worth a filter.</summary>
    public double MinDeviationDb { get; init; } = 2.0;

    /// <summary>
    /// How many filters at most, largest error first. A long tail of small
    /// corrections is where a room correction stops being conservative.
    /// </summary>
    public int MaxFilters { get; init; } = 6;

    /// <summary>
    /// Octaves averaged before anything is fitted, wider than the sixth of
    /// an octave the curve is drawn at. What is fitted must be the shape of
    /// the room, and a third of an octave is about where the interference
    /// pattern stops and the room begins.
    /// </summary>
    public double FitSmoothingOctave { get; init; } = 1.0 / 3.0;

    public void Validate()
    {
        if (LowHz <= 0 || HighHz <= LowHz)
        {
            throw new ArgumentOutOfRangeException(nameof(LowHz), "The band must rise.");
        }
        if (MaxBoostDb < 0 || MaxCutDb < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBoostDb), "The limits are magnitudes.");
        }
        if (MaxQ <= 0 || MaxFilters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxQ), "Q and the filter count are positive.");
        }
    }
}

/// <summary>A correction, and what it is expected to do.</summary>
public sealed record CorrectionProfile
{
    public required IReadOnlyList<Biquad> Filters { get; init; }

    /// <summary>
    /// Attenuation applied before the filters, to make room for their
    /// boosts.
    /// </summary>
    /// <remarks>
    /// Not optional and not cosmetic. The ring holds full-scale audio and
    /// volume only attenuates, so a band boosted by 3 dB on material
    /// already near full scale overflows — and it presents as distortion on
    /// loud passages only, which is the worst way to find out. The sum of
    /// the boosts rather than the largest of them, because two boosts close
    /// together add.
    /// </remarks>
    public required double PreampDb { get; init; }

    /// <summary>The band that was allowed to be touched.</summary>
    public required double LowHz { get; init; }

    public required double HighHz { get; init; }

    /// <summary>What the response should look like afterwards, per point.</summary>
    public required IReadOnlyList<double> PredictedDb { get; init; }

    public required IReadOnlyList<double> FrequenciesHz { get; init; }

    /// <summary>
    /// How far the in-band response sits from flat, before and after, as
    /// an RMS in dB. The one number that says whether the correction is
    /// worth applying — and it is computed from the measurement, so it is
    /// a prediction rather than a result.
    /// </summary>
    public required double DeviationBeforeDb { get; init; }

    public required double DeviationAfterDb { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Fits a conservative correction to a measured response.
/// </summary>
/// <remarks>
/// <para>
/// <b>Do not invert the measurement.</b> That is the one instruction the
/// original proposal gives about this stage and it is the whole design.
/// A room's response contains things that can be fixed — a mode that rings
/// and adds ten decibels of boom at one note — and things that cannot: a
/// cancellation where a reflection arrives out of phase, which no amount of
/// power fills because the extra power cancels too. Inverting the
/// measurement tries to fix both, wastes the amplifier on the second, and
/// makes every other seat in the room worse.
/// </para>
/// <para>
/// So this fits a few broad peaking filters inside a band it is allowed to
/// touch, cuts more readily than it boosts, refuses anything narrow, and
/// stops. Everything it refuses to do is listed in the profile's notes, so
/// a person can see what was left alone and why.
/// </para>
/// </remarks>
public static class RoomCorrection
{
    public static CorrectionProfile Fit(
        RoomResponse response, CorrectionRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        rules ??= new CorrectionRules();
        rules.Validate();

        var hz = response.FrequenciesHz;
        var notes = new List<string>();

        foreach (var warning in response.Warnings)
        {
            notes.Add($"The measurement said: {warning}");
        }

        var smoothed = Smooth(hz, response.MagnitudeDb, rules.FitSmoothingOctave);

        // The band is levelled on its own average rather than on the
        // measurement's 200 Hz-2 kHz reference. Correcting towards a flat
        // *bass* means flat relative to what the bass already averages; a
        // profile that lifted the whole bottom end to meet the midrange
        // would be a tone control, not a room correction.
        var inBand = new List<int>();
        for (int i = 0; i < hz.Count; i++)
        {
            if (hz[i] >= rules.LowHz && hz[i] <= rules.HighHz)
            {
                inBand.Add(i);
            }
        }
        if (inBand.Count < 8)
        {
            notes.Add($"There are too few measured points between {rules.LowHz:0} and "
                + $"{rules.HighHz:0} Hz to fit anything.");
            return Empty(response, rules, notes);
        }

        /*
         * The median, not the mean.
         *
         * The correction aims at the band's own baseline, and a mean is
         * dragged upwards by exactly the thing being removed: one +12 dB
         * mode occupying a fifth of the band lifts the average by two
         * decibels, so the fitter aims two decibels high and under-cuts
         * the mode by that much. A median is indifferent to a feature that
         * occupies a minority of the band, which is the definition of a
         * mode worth correcting.
         */
        double level = Baseline(smoothed, inBand);
        var error = new double[hz.Count];
        var raw = new double[hz.Count];
        for (int i = 0; i < hz.Count; i++)
        {
            error[i] = smoothed[i] - level;
            raw[i] = response.MagnitudeDb[i] - level;
        }

        var candidates = new List<Biquad>();
        foreach (int i in Features(error, inBand))
        {
            double deviation = error[i];
            if (Math.Abs(deviation) < rules.MinDeviationDb)
            {
                continue;
            }

            /*
             * How much to correct comes from the smoothed curve; how narrow
             * the thing is comes from the unsmoothed one.
             *
             * They cannot both come from the same place. Smoothing is what
             * makes the size of a correction a property of the room rather
             * than of the interference pattern — but it is also exactly
             * what destroys the evidence that something is narrow. A Q of
             * 20 averaged over a third of an octave looks like a Q of 4,
             * and the rule that refuses narrow features then never fires.
             */
            double q = QualityOf(hz, raw, i, inBand[0], inBand[^1]);
            if (q > rules.MaxQ)
            {
                notes.Add($"Left the {(deviation > 0 ? "peak" : "dip")} at {hz[i]:0} Hz alone: "
                    + $"Q {q:0.0} is narrower than {rules.MaxQ:0}, so it belongs to where the "
                    + "microphone stood rather than to the room.");
                continue;
            }

            double gain = -deviation;
            if (gain > 0)
            {
                gain *= rules.DipRestraint;
            }
            double clamped = Math.Clamp(gain, -rules.MaxCutDb, rules.MaxBoostDb);
            if (Math.Abs(clamped) < 0.5)
            {
                continue;
            }
            if (Math.Abs(clamped - gain) > 0.05)
            {
                notes.Add($"Held the correction at {hz[i]:0} Hz to "
                    + $"{(clamped > 0 ? "+" : "")}{clamped:0.0} dB; the measurement asked for "
                    + $"{(gain > 0 ? "+" : "")}{gain:0.0} dB.");
            }

            candidates.Add(new Biquad
            {
                FrequencyHz = Math.Round(hz[i], 1),
                Q = Math.Round(Math.Max(0.5, q), 2),
                GainDb = Math.Round(clamped, 1),
                SampleRate = response.SampleRate,
            });
        }

        var filters = candidates
            .OrderByDescending(f => Math.Abs(f.GainDb))
            .Take(rules.MaxFilters)
            .OrderBy(f => f.FrequencyHz)
            .ToList();

        if (candidates.Count > filters.Count)
        {
            notes.Add($"Fitted the {filters.Count} largest of {candidates.Count} corrections; "
                + "a long tail of small ones is where this stops being conservative.");
        }

        /*
         * Headroom for the worst the filters do TOGETHER at any one
         * frequency — not the sum of their gains.
         *
         * The sum is a bound and a bad one: four boosts at 38, 46, 82 and
         * 254 Hz added to 9.2 dB on a real measurement here, which would
         * have thrown away most of the loudspeaker's output to protect
         * against an overlap that does not exist. Filters an octave apart
         * barely reach each other. The combined magnitude response is
         * exactly what the audio will see, it is already being computed for
         * the predicted curve, and its peak is the honest answer.
         */
        var predicted = new double[hz.Count];
        double worst = 0;
        for (int i = 0; i < hz.Count; i++)
        {
            double combined = filters.Sum(f => f.MagnitudeDb(hz[i]));
            predicted[i] = response.MagnitudeDb[i] + combined;
            worst = Math.Max(worst, combined);
        }
        double preamp = -Math.Ceiling(worst * 2) / 2;   // to the next half dB

        return new CorrectionProfile
        {
            Filters = filters,
            PreampDb = preamp,
            LowHz = rules.LowHz,
            HighHz = rules.HighHz,
            FrequenciesHz = hz,
            PredictedDb = predicted,
            DeviationBeforeDb = Deviation(response.MagnitudeDb, inBand, level),
            DeviationAfterDb = Deviation(predicted, inBand, Baseline(predicted, inBand)),
            Notes = notes,
        };
    }

    private static CorrectionProfile Empty(
        RoomResponse response, CorrectionRules rules, List<string> notes) => new()
    {
        Filters = [],
        PreampDb = 0,
        LowHz = rules.LowHz,
        HighHz = rules.HighHz,
        FrequenciesHz = response.FrequenciesHz,
        PredictedDb = response.MagnitudeDb,
        DeviationBeforeDb = 0,
        DeviationAfterDb = 0,
        Notes = notes,
    };

    /// <summary>The band's own middle, robust to what is being corrected.</summary>
    private static double Baseline(IReadOnlyList<double> db, List<int> inBand)
    {
        var sorted = inBand.Select(i => db[i]).Order().ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static double Deviation(IReadOnlyList<double> db, List<int> inBand, double level) =>
        Math.Sqrt(inBand.Average(i => (db[i] - level) * (db[i] - level)));

    /// <summary>
    /// A wider average than the curve is drawn at, in octaves. What gets
    /// fitted has to be the shape of the room rather than the interference
    /// pattern laid over it.
    /// </summary>
    private static double[] Smooth(
        IReadOnlyList<double> hz, IReadOnlyList<double> db, double octaves)
    {
        var smoothed = new double[db.Count];
        double half = Math.Pow(2.0, octaves / 2.0);

        for (int i = 0; i < db.Count; i++)
        {
            double sum = 0;
            int count = 0;
            for (int j = 0; j < db.Count; j++)
            {
                if (hz[j] >= hz[i] / half && hz[j] <= hz[i] * half)
                {
                    sum += db[j];
                    count++;
                }
            }
            smoothed[i] = count > 0 ? sum / count : db[i];
        }
        return smoothed;
    }

    /// <summary>Indices of the local peaks and dips inside the band.</summary>
    private static IEnumerable<int> Features(double[] error, List<int> inBand)
    {
        for (int k = 1; k < inBand.Count - 1; k++)
        {
            int i = inBand[k];
            bool peak = error[i] >= error[i - 1] && error[i] >= error[i + 1] && error[i] > 0;
            bool dip = error[i] <= error[i - 1] && error[i] <= error[i + 1] && error[i] < 0;
            if (peak || dip)
            {
                yield return i;
            }
        }
    }

    /// <summary>
    /// How narrow a feature is, as f0 divided by its width at half its own
    /// height. Walked outwards from the extreme in both directions; a
    /// feature that never comes back down inside the band is treated as
    /// broad, which is the safe direction — broad features get corrected
    /// gently and narrow ones get refused.
    /// </summary>
    private static double QualityOf(
        IReadOnlyList<double> hz, double[] error, int at, int first, int last)
    {
        double half = error[at] / 2.0;

        int low = at;
        while (low > first && Math.Abs(error[low]) > Math.Abs(half))
        {
            low--;
        }
        int high = at;
        while (high < last && Math.Abs(error[high]) > Math.Abs(half))
        {
            high++;
        }

        double width = hz[high] - hz[low];
        if (width <= 0)
        {
            return double.MaxValue;
        }
        return hz[at] / width;
    }
}
