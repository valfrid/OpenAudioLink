using OpenAudioLink.Core.Audio;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class LatencyProfileTests
{
    /// <summary>
    /// The whole point of a profile is that the node does not quietly
    /// change it.
    /// </summary>
    /// <remarks>
    /// The firmware clamps rather than refuses: a target too large for its
    /// ring is silently reduced, and the operator runs a buffer nobody
    /// chose with nothing to say so. Two ceilings apply and the stricter
    /// one is not the obvious one — <c>target ≤ ring × 3/4</c> is the rule
    /// that gets quoted, but <c>trim_above ≤ capacity × 7/8</c> works out
    /// at <c>ring ≥ 1.72 × target</c> and binds first.
    /// </remarks>
    [Fact]
    public void Every_profile_fits_its_own_ring() =>
        Assert.All(LatencyProfile.All, p => Assert.True(
            p.Fits, $"{p.Id}: target {p.TargetMs} does not fit ring {p.RingMs}"));

    /// <summary>
    /// Restates the firmware's arithmetic (oal_playout.c, apply_target) so
    /// that a change on either side shows up here rather than in a
    /// listening test.
    /// </summary>
    [Theory]
    [InlineData("short", 200, 100, 0, 75, 150, 112)]
    [InlineData("standard", 400, 200, 100, 150, 300, 225)]
    [InlineData("long", 1000, 550, 450, 412, 825, 618)]
    public void The_bands_match_the_firmware(
        string id, int ring, int target, int delay, int pad, int trim, int steer)
    {
        var p = LatencyProfile.ById(id);
        Assert.NotNull(p);
        Assert.Equal(ring, p.RingMs);
        Assert.Equal(target, p.TargetMs);
        Assert.Equal(delay, p.DelayMs);
        Assert.Equal(pad, p.PadBelowMs);
        Assert.Equal(trim, p.TrimAboveMs);
        Assert.Equal(steer, p.SteerToMs);
    }

    /// <summary>
    /// Measured, not derived: run 40 reported <c>steerMs</c> 225 against a
    /// 200 ms target and a median fill of 225 ms on both nodes.
    /// </summary>
    /// <remarks>
    /// TUNING.md said 1.5 × target for several releases, which was true
    /// before firmware 0.38.0 added steering and made the fill rest at the
    /// middle of the quiet band instead of riding the trim line. Every
    /// latency figure in that document was a third too high. This test is
    /// the reason it cannot drift back.
    /// </remarks>
    [Fact]
    public void The_fill_rests_at_nine_eighths_of_target_not_three_halves()
    {
        var standard = LatencyProfile.ById("standard")!;
        Assert.Equal(225, standard.SteerToMs);
        Assert.NotEqual(standard.TargetMs * 3 / 2, standard.SteerToMs);
    }

    /// <summary>
    /// The long profile exists to ride out the gaps this project has
    /// actually measured. The worst recorded is 419 ms, in run 39.
    /// </summary>
    [Fact]
    public void Long_survives_a_longer_gap_than_any_yet_measured() =>
        Assert.True(LatencyProfile.ById("long")!.SurvivesGapMs > 419);

    /// <summary>
    /// Short is the firmware's floor. <c>delayMs</c> 0 leaves the compiled
    /// 100 ms target, and there is nothing further to remove.
    /// </summary>
    [Fact]
    public void Short_is_the_floor() =>
        Assert.Equal(0, LatencyProfile.ById("short")!.DelayMs);

    [Fact]
    public void Profiles_are_offered_shortest_first() =>
        Assert.Equal(
            LatencyProfile.All.Select(p => p.SteerToMs).OrderBy(x => x),
            LatencyProfile.All.Select(p => p.SteerToMs));

    [Theory]
    [InlineData("STANDARD")]
    [InlineData("Standard")]
    public void Lookup_ignores_case(string id) =>
        Assert.Equal("standard", LatencyProfile.ById(id)?.Id);

    [Theory]
    [InlineData("medium")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_unknown(string? id) =>
        Assert.Null(LatencyProfile.ById(id));

    /// <summary>
    /// Matching is on what the node reports, so a node configured by hand
    /// or by an older Hub still answers honestly.
    /// </summary>
    [Fact]
    public void A_node_is_matched_on_its_reported_settings()
    {
        Assert.Equal("standard", LatencyProfile.Match(400, 200)?.Id);
        Assert.Equal("long", LatencyProfile.Match(1000, 550)?.Id);
    }

    /// <summary>
    /// A node part-way through a profile change — ring stored for the next
    /// boot, delay already applied — is running neither profile, and
    /// saying so is more useful than picking the nearer one.
    /// </summary>
    [Fact]
    public void A_half_applied_profile_matches_nothing()
    {
        Assert.Null(LatencyProfile.Match(400, 550));
        Assert.Null(LatencyProfile.Match(1000, 200));
    }

    [Fact]
    public void An_unreported_setting_matches_nothing()
    {
        Assert.Null(LatencyProfile.Match(null, 200));
        Assert.Null(LatencyProfile.Match(400, null));
    }

    /// <summary>
    /// A USB dongle holds about 100 ms in its host driver against an I²S
    /// DAC's 20, which is the difference alignment exists to correct.
    /// </summary>
    [Fact]
    public void The_output_stage_is_part_of_the_latency()
    {
        var standard = LatencyProfile.ById("standard")!;
        Assert.Equal(245, standard.AirToEarMs(usbOutput: false));
        Assert.Equal(325, standard.AirToEarMs(usbOutput: true));
    }

    /// <summary>
    /// 1000 ms of 24-bit stereo at 48 kHz is 375 kB, on a part with 8 MB of
    /// PSRAM. Worth stating because "a one-second buffer" sounds expensive
    /// and is not.
    /// </summary>
    [Fact]
    public void The_longest_ring_costs_under_half_a_megabyte() =>
        Assert.True(LatencyProfile.ById("long")!.RingBytes < 400 * 1024);
}
