namespace OpenAudioLink.Core.Devices;

/// <summary>
/// Which of the stream's two channels a Consumer plays (decision 10).
///
/// The stream is stereo and byte-identical to every destination, because
/// that identity is what lets several speakers stay in step. What a node
/// does with the two channels is a property of the node — a single mono
/// speaker in a kitchen is a first-class arrangement for this project, not
/// a degraded stereo one.
///
/// These names match the firmware's <c>oal_channel_t</c> exactly; they are
/// what travels in <c>/status</c> and <c>/config</c>.
/// </summary>
public static class AudioChannel
{
    /// <summary>Both channels, as they arrive. One node, two speakers.</summary>
    public const string Stereo = "stereo";

    /// <summary>(L+R)/2 on both outputs. One node, one speaker.</summary>
    public const string Mono = "mono";

    /// <summary>Left on both outputs. One half of a pair.</summary>
    public const string Left = "left";

    /// <summary>Right on both outputs. The other half.</summary>
    public const string Right = "right";
}

/// <summary>Helpers over the channel names, kept apart so the constants read plainly.</summary>
public static class AudioChannels
{
    /// <summary>
    /// In the order a person chooses between them: whole rooms first, then
    /// the halves of a pair, which are the rarer answer.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
        [AudioChannel.Stereo, AudioChannel.Mono, AudioChannel.Left, AudioChannel.Right];

    public static bool IsKnown(string? channel) =>
        channel is not null && All.Contains(channel);

    /// <summary>
    /// A stereo pair costs a synchronisation problem that one node does
    /// not: two channels off one DAC share a clock and cannot drift from
    /// each other, while two nodes are two crystals. Worth saying in the
    /// interface, since the choice is made once and lived with.
    /// </summary>
    public static bool IsHalfOfAPair(string? channel) =>
        channel is AudioChannel.Left or AudioChannel.Right;
}
