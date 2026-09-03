namespace OpenAudioLink.Core.Audio;

/// <summary>
/// One second-order section, as a peaking filter.
/// </summary>
/// <remarks>
/// <para>
/// Peaking only, deliberately. Modal room correction is a matter of taking
/// a few decibels out of a few resonances, and a peaking filter is exactly
/// that shape. Shelves and crossovers would be a different feature with
/// different rules, and adding them here would invite the correction to
/// re-voice a loudspeaker rather than fix a room.
/// </para>
/// <para>
/// The design formulas are Robert Bristow-Johnson's audio EQ cookbook, the
/// same ones every implementation of this uses — including the one the node
/// will run. Both ends deriving coefficients from f0, Q and gain rather
/// than shipping five floats around means a profile can be read by a person
/// and checked by eye.
/// </para>
/// </remarks>
public sealed record Biquad
{
    public required double FrequencyHz { get; init; }

    /// <summary>
    /// How narrow. Bandwidth is f0/Q, so a big Q is a narrow filter — and
    /// a narrow filter is usually the wrong answer to a room, which is why
    /// the fitter caps it rather than matching whatever it measures.
    /// </summary>
    public required double Q { get; init; }

    public required double GainDb { get; init; }

    public required int SampleRate { get; init; }

    /// <summary>
    /// The five coefficients, normalised by a0, in the order a direct form
    /// II transposed implementation wants them.
    /// </summary>
    public (double B0, double B1, double B2, double A1, double A2) Coefficients()
    {
        double a = Math.Pow(10.0, GainDb / 40.0);
        double w = 2.0 * Math.PI * FrequencyHz / SampleRate;
        double alpha = Math.Sin(w) / (2.0 * Q);
        double cos = Math.Cos(w);

        double b0 = 1 + alpha * a;
        double b1 = -2 * cos;
        double b2 = 1 - alpha * a;
        double a0 = 1 + alpha / a;
        double a1 = -2 * cos;
        double a2 = 1 - alpha / a;

        return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>
    /// What this filter does at one frequency, in dB.
    /// </summary>
    /// <remarks>
    /// Closed form rather than a swept measurement, so the predicted result
    /// of a correction can be drawn beside the measurement it was fitted to
    /// <em>before</em> anything is written to a loudspeaker. Seeing what a
    /// correction intends to do is what makes it possible to refuse one.
    /// </remarks>
    public double MagnitudeDb(double hz)
    {
        var (b0, b1, b2, a1, a2) = Coefficients();
        double w = 2.0 * Math.PI * hz / SampleRate;
        double cos1 = Math.Cos(w);
        double cos2 = Math.Cos(2 * w);

        double numerator = b0 * b0 + b1 * b1 + b2 * b2
            + 2 * (b0 * b1 + b1 * b2) * cos1 + 2 * b0 * b2 * cos2;
        double denominator = 1 + a1 * a1 + a2 * a2
            + 2 * (a1 + a1 * a2) * cos1 + 2 * a2 * cos2;

        if (denominator <= 0 || numerator <= 0)
        {
            return 0;
        }
        return 10.0 * Math.Log10(numerator / denominator);
    }

    public override string ToString() =>
        $"{FrequencyHz:0.#} Hz, Q {Q:0.00}, {(GainDb > 0 ? "+" : "")}{GainDb:0.0} dB";
}
