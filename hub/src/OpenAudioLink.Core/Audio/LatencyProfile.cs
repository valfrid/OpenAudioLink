namespace OpenAudioLink.Core.Audio;

/// <summary>
/// A named buffer setting: how much audio a node holds, and therefore how
/// far behind the room it plays.
/// </summary>
/// <remarks>
/// <para>
/// The two knobs underneath — <c>ringMs</c> (capacity) and <c>delayMs</c>
/// (depth) — are the right primitives and the wrong interface. They fail
/// differently, only one of them needs a reboot, and choosing a pair that
/// works together means knowing three firmware constants. Run 40 ended
/// with the observation that the buffer is a *decision nobody had made*
/// rather than a fault: 225 ms of cushion against a network measured
/// leaving 200 ms holes, while Snapcast ships 1000 and AirPlay 2000.
/// </para>
/// <para>
/// So this is the decision, made three times and named, with the
/// arithmetic done once here instead of in whoever is reading TUNING.md at
/// the time.
/// </para>
/// </remarks>
public sealed record LatencyProfile
{
    /// <summary>Stable identifier, used by the API and stored per node.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>When this is the right choice, in one line.</summary>
    public required string Use { get; init; }

    /// <summary>Ring capacity in milliseconds. Applies at the node's next boot.</summary>
    public required int RingMs { get; init; }

    /// <summary>
    /// Where the fill aims, in milliseconds. The node computes this as
    /// <c>100 + delayMs</c>, so <see cref="DelayMs"/> is what actually gets
    /// sent.
    /// </summary>
    public required int TargetMs { get; init; }

    /// <summary>
    /// The compiled-in target every node starts from, which
    /// <c>delayMs</c> adds to. Not a preference — it is
    /// <c>TARGET_MS_DEFAULT</c> in the firmware, and the two must agree.
    /// </summary>
    public const int BaseTargetMs = 100;

    /// <summary>What to send as <c>delayMs</c> for a node with no alignment offset.</summary>
    public int DelayMs => TargetMs - BaseTargetMs;

    /*
     * The three numbers below are the firmware's, restated. They are here
     * so a profile can be checked before it is offered rather than after a
     * node has rebooted into it, and they are the reason `Fits` exists.
     *
     * oal_playout.c, apply_target():
     *     trim_above = target * 3/2, clamped to capacity * 7/8
     *     pad_below  = target * 3/4
     *     steer_to   = (pad_below + trim_above) / 2
     */

    /// <summary>Where trimming begins.</summary>
    public int TrimAboveMs => TargetMs * 3 / 2;

    /// <summary>Where padding begins.</summary>
    public int PadBelowMs => TargetMs * 3 / 4;

    /// <summary>
    /// Where the fill actually comes to rest — the midpoint of the quiet
    /// band, and the number that decides latency.
    /// </summary>
    /// <remarks>
    /// <b>Not 1.5 × target.</b> That was true before firmware 0.38.0 added
    /// steering, when the fill rode the trim line, and TUNING.md went on
    /// saying it for several releases afterwards — overstating every
    /// latency figure in the document by a third. Steering parks the ring
    /// at <c>(pad_below + trim_above) / 2</c>, which is 1.125 × target.
    /// Confirmed against run 40: a 200 ms target reported <c>steerMs</c>
    /// 225 and measured a median fill of 225 ms on both nodes.
    /// </remarks>
    public int SteerToMs => (PadBelowMs + TrimAboveMs) / 2;

    /// <summary>
    /// How long a silent gap the node can ride out before the ring runs
    /// dry. This is the number the profile exists to move: run 40 measured
    /// 1 329 and 1 583 gaps in the 100-200 ms band over five hours, and
    /// 14 and 27 above 200 ms.
    /// </summary>
    public int SurvivesGapMs => SteerToMs;

    /// <summary>
    /// Roughly what a listener experiences, adding the output stage: about
    /// 20 ms for an I²S DAC's DMA descriptors, about 100 ms for a USB
    /// dongle's host driver.
    /// </summary>
    public int AirToEarMs(bool usbOutput) => SteerToMs + (usbOutput ? 100 : 20);

    /// <summary>
    /// Whether the ring is big enough for this target, by the firmware's
    /// own two rules.
    /// </summary>
    /// <remarks>
    /// A profile that fails this is not rejected by the node — it is
    /// silently clamped, and the operator gets a buffer they did not
    /// choose with no indication that anything happened. The binding
    /// constraint is the trim line rather than the target ceiling:
    /// <c>capacity * 7/8 ≥ target * 3/2</c> works out at
    /// <c>ring ≥ 1.72 × target</c>, which is stricter than the
    /// <c>ring ≥ 1.34 × target</c> the three-quarters rule asks for.
    /// </remarks>
    public bool Fits =>
        TargetMs <= RingMs * 3 / 4 && TargetMs * 3 / 2 <= RingMs - RingMs / 8;

    /// <summary>Ring memory at 48 kHz, 24-bit stereo, as the node allocates it.</summary>
    public int RingBytes => RingMs * 48 * 2 * 4;

    /// <summary>
    /// The three, shortest first.
    /// </summary>
    /// <remarks>
    /// Chosen against measurement rather than roundness. <b>Long</b> is
    /// sized so its resting fill (618 ms) exceeds every arrival gap this
    /// project has ever recorded — the worst was 419 ms, in run 39 — with
    /// margin, and it is the largest that fits the 1000 ms ring ceiling
    /// without the firmware clamping the trim line. <b>Short</b> is the
    /// firmware's own floor: <c>delayMs</c> 0, the compiled 100 ms target,
    /// nothing removable left. <b>Standard</b> is what runs today, kept
    /// unchanged so that every measurement from run 34 onward still
    /// describes it.
    /// </remarks>
    public static readonly IReadOnlyList<LatencyProfile> All =
    [
        new()
        {
            Id = "short",
            Name = "Short",
            Use = "Video and TV, where lip-sync matters more than robustness. "
                + "Realistic only on a wired backhaul: it survives a 112 ms gap, "
                + "and Wi-Fi here routinely leaves longer ones.",
            RingMs = 200,
            TargetMs = 100,
        },
        new()
        {
            Id = "standard",
            Name = "Standard",
            Use = "The default, and what every measurement in "
                + "LINK-MEASUREMENTS.md from run 34 onward describes.",
            RingMs = 400,
            TargetMs = 200,
        },
        new()
        {
            Id = "long",
            Name = "Long",
            Use = "Music, where nobody can perceive the delay. Rides out a "
                + "618 ms gap — longer than any this project has measured — "
                + "at the cost of two thirds of a second behind the room.",
            RingMs = 1000,
            TargetMs = 550,
        },
    ];

    public static LatencyProfile? ById(string? id) =>
        id is null ? null : All.FirstOrDefault(
            p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Which profile a node is currently running, or null if its settings
    /// match none of them.
    /// </summary>
    /// <remarks>
    /// Matched on the ring and target the node reports rather than on
    /// anything the Hub stored, so a node configured by hand, by an older
    /// Hub, or before this existed still reports honestly. A node part-way
    /// through a profile change — ring set, not yet rebooted — matches
    /// nothing, which is correct: it is running neither.
    /// </remarks>
    public static LatencyProfile? Match(int? ringMs, int? targetMs) =>
        ringMs is null || targetMs is null
            ? null
            : All.FirstOrDefault(p => p.RingMs == ringMs && p.TargetMs == targetMs);
}
