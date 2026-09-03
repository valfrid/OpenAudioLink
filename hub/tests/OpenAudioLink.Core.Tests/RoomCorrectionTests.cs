using OpenAudioLink.Core.Audio;
using Xunit;

namespace OpenAudioLink.Core.Tests;

/// <summary>
/// Every test here is one of the conservative-correction rules, because
/// the rules <em>are</em> the feature. Fitting a filter to a measurement is
/// easy; the whole difficulty is refusing to fit the ones that would make
/// the room worse, and a rule with no test is a rule that will quietly stop
/// applying.
/// </summary>
public class RoomCorrectionTests
{
    private const int Rate = 48000;

    /// <summary>A response built from a peak or dip laid on a flat room.</summary>
    private static RoomResponse RoomWith(params (double Hz, double Q, double Db)[] features)
    {
        var hz = new double[240];
        var db = new double[240];
        for (int i = 0; i < hz.Length; i++)
        {
            hz[i] = 20 * Math.Exp(Math.Log(1000) * i / 239);
            db[i] = features.Sum(f =>
                new Biquad { FrequencyHz = f.Hz, Q = f.Q, GainDb = f.Db, SampleRate = Rate }
                    .MagnitudeDb(hz[i]));
        }

        return new RoomResponse
        {
            FrequenciesHz = hz,
            MagnitudeDb = db,
            ImpulseResponse = [],
            SampleRate = Rate,
            CyclesAveraged = 7,
            SignalToNoiseDb = 27,
            PeakDbFs = -35,
            ClippedSamples = 0,
            ImpulsePeakSeconds = 0.25,
            AlignMarginSeconds = 0.25,
            Warnings = [],
        };
    }

    private static double At(CorrectionProfile p, IReadOnlyList<double> values, double hz)
    {
        int nearest = 0;
        double best = double.MaxValue;
        for (int i = 0; i < p.FrequenciesHz.Count; i++)
        {
            double away = Math.Abs(Math.Log(p.FrequenciesHz[i] / hz));
            if (away < best) { best = away; nearest = i; }
        }
        return values[nearest];
    }

    /// <summary>
    /// The measurement this whole feature exists to fix, and the one both
    /// loudspeakers here actually showed: a broad mode adding twelve
    /// decibels of boom at one note.
    /// </summary>
    [Fact]
    public void A_broad_mode_is_cut_and_the_room_comes_out_flatter()
    {
        var profile = RoomCorrection.Fit(RoomWith((100, 2, 12)));

        var filter = Assert.Single(profile.Filters);
        Assert.Equal(100.0, filter.FrequencyHz, 12.0);
        Assert.True(filter.GainDb < -5, $"a +12 dB mode should be cut, not {filter.GainDb:0.0} dB");

        Assert.True(profile.DeviationAfterDb < profile.DeviationBeforeDb * 0.5,
            $"correction should more than halve the deviation: "
            + $"{profile.DeviationBeforeDb:0.00} dB -> {profile.DeviationAfterDb:0.00} dB");

        // And it should have flattened the peak itself, not moved it. The
        // room was +12 dB here and the rules allow a 9 dB cut, so what is
        // left should be about 3 — which is only true if the fitter aimed
        // at the band's baseline rather than at an average the mode itself
        // had lifted.
        Assert.True(Math.Abs(At(profile, profile.PredictedDb, 100)) < 4.0,
            $"predicted {At(profile, profile.PredictedDb, 100):0.0} dB at 100 Hz "
            + $"from {profile.Filters[0]}");
    }

    /// <summary>
    /// The most important rule. Above about 300 Hz what is measured is a
    /// property of where the microphone stood rather than of the room, and
    /// it is also where this system stops being able to tell the room from
    /// the microphone.
    /// </summary>
    [Fact]
    public void Nothing_above_the_band_is_touched()
    {
        var profile = RoomCorrection.Fit(RoomWith((5000, 1.5, 12), (12000, 1, 10)));

        Assert.Empty(profile.Filters);
        Assert.Equal(0.0, profile.PreampDb);
    }

    [Fact]
    public void Nothing_below_the_band_is_touched()
    {
        // 22 Hz is under the 30 Hz floor: the sweep is weakest there, the
        // microphone is outside its specified range, and the two speakers
        // measured here disagreed by 20 dB in that octave.
        var profile = RoomCorrection.Fit(RoomWith((22, 3, 14)));
        Assert.Empty(profile.Filters);
    }

    /// <summary>
    /// A narrow null moves when the microphone moves, so a filter fitted to
    /// one is a filter fitted to the tripod.
    /// </summary>
    [Fact]
    public void A_narrow_null_is_refused_and_the_refusal_is_explained()
    {
        var profile = RoomCorrection.Fit(RoomWith((120, 20, -18)));

        Assert.Empty(profile.Filters);
        Assert.Contains(profile.Notes, n => n.Contains("microphone stood"));
    }

    /// <summary>
    /// Boost costs amplifier power and excursion, and a dip that deep is a
    /// cancellation that no amount of power fills.
    /// </summary>
    [Fact]
    public void A_deep_dip_is_not_filled_in()
    {
        var profile = RoomCorrection.Fit(RoomWith((90, 2, -15)));

        foreach (var filter in profile.Filters)
        {
            Assert.True(filter.GainDb <= 3.0 + 1e-9,
                $"boost held to +3 dB, not {filter.GainDb:0.0}");
        }
        Assert.Contains(profile.Notes, n => n.Contains("Held the correction"));
    }

    [Fact]
    public void A_dip_gets_less_correction_than_an_equal_peak()
    {
        // Small enough that neither hits a limit, so what is compared is
        // the restraint itself rather than the clamp.
        var peak = RoomCorrection.Fit(RoomWith((100, 2, 4)));
        var dip = RoomCorrection.Fit(RoomWith((100, 2, -4)));

        double cut = Math.Abs(Assert.Single(peak.Filters).GainDb);
        double lift = Math.Abs(Assert.Single(dip.Filters).GainDb);

        Assert.True(lift < cut * 0.75,
            $"a dip should be corrected more gently than a peak: {lift:0.0} against {cut:0.0} dB");
    }

    [Fact]
    public void An_enormous_peak_is_cut_only_as_far_as_the_rules_allow()
    {
        var profile = RoomCorrection.Fit(RoomWith((80, 2, 20)));

        foreach (var filter in profile.Filters)
        {
            Assert.True(filter.GainDb >= -9.0 - 1e-9,
                $"cut held to -9 dB, not {filter.GainDb:0.0}");
        }
    }

    /// <summary>
    /// The ring holds full-scale audio and volume only attenuates, so a
    /// band boosted on material already near full scale overflows — and it
    /// presents as distortion on loud passages only, which is the worst way
    /// to find out.
    /// </summary>
    [Fact]
    public void Every_boost_is_paid_for_with_headroom()
    {
        var profile = RoomCorrection.Fit(RoomWith((70, 1.5, -8), (160, 1.5, -7)));

        Assert.Contains(profile.Filters, f => f.GainDb > 0);

        // The worst the filters do TOGETHER at any one frequency, which is
        // exactly what the audio sees — not the sum of their gains, which
        // would give away most of a loudspeaker's output to protect against
        // an overlap that two filters an octave apart do not have.
        double worst = profile.FrequenciesHz
            .Select(hz => profile.Filters.Sum(f => f.MagnitudeDb(hz)))
            .Max();
        Assert.True(worst > 0, "this room should have asked for some boost");

        // Nothing anywhere may still gain after the preamp. This is the
        // property, and it is checked at every measured frequency rather
        // than argued about.
        for (int i = 0; i < profile.FrequenciesHz.Count; i++)
        {
            double through = profile.PreampDb
                + profile.Filters.Sum(f => f.MagnitudeDb(profile.FrequenciesHz[i]));
            Assert.True(through <= 1e-9,
                $"{profile.FrequenciesHz[i]:0} Hz still gains {through:0.00} dB after the preamp");
        }
    }

    [Fact]
    public void A_room_that_is_already_flat_is_left_alone()
    {
        var profile = RoomCorrection.Fit(RoomWith());

        Assert.Empty(profile.Filters);
        Assert.Equal(0.0, profile.PreampDb);
        Assert.Equal(profile.DeviationBeforeDb, profile.DeviationAfterDb, 6);
    }

    [Fact]
    public void A_ripple_smaller_than_the_threshold_is_not_worth_a_filter()
    {
        var profile = RoomCorrection.Fit(RoomWith((100, 2, 1.5)));
        Assert.Empty(profile.Filters);
    }

    /// <summary>
    /// A long tail of small corrections is where a room correction stops
    /// being conservative.
    /// </summary>
    [Fact]
    public void The_number_of_filters_is_capped_at_the_largest_errors()
    {
        var profile = RoomCorrection.Fit(
            RoomWith((40, 3, 8), (55, 3, -7), (75, 3, 9), (100, 3, -8),
                     (140, 3, 10), (190, 3, -6), (250, 3, 7)),
            new CorrectionRules { MaxFilters = 3 });

        Assert.Equal(3, profile.Filters.Count);
        Assert.Contains(profile.Notes, n => n.Contains("largest of"));
        // In frequency order, so a person can read the profile.
        Assert.Equal(profile.Filters.Select(f => f.FrequencyHz).Order(),
            profile.Filters.Select(f => f.FrequencyHz));
    }

    [Fact]
    public void The_measurements_own_doubts_are_carried_into_the_profile()
    {
        var response = RoomWith((100, 2, 12)) with
        {
            Warnings = ["12 sample(s) reached full scale — the curve includes the clipping."],
        };

        var profile = RoomCorrection.Fit(response);
        Assert.Contains(profile.Notes, n => n.Contains("full scale"));
    }

    [Fact]
    public void The_band_that_was_allowed_is_reported_with_the_answer()
    {
        var profile = RoomCorrection.Fit(
            RoomWith((100, 2, 8)), new CorrectionRules { LowHz = 40, HighHz = 200 });

        Assert.Equal(40, profile.LowHz);
        Assert.Equal(200, profile.HighHz);
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(300, 30)]
    public void A_band_that_does_not_rise_is_refused(double low, double high)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RoomCorrection.Fit(RoomWith(), new CorrectionRules { LowHz = low, HighHz = high }));
    }
}
