namespace OpenAudioLink.Core.Radio;

/// <summary>
/// A saved internet radio station: a name somebody chose and the URL behind
/// it.
/// </summary>
/// <remarks>
/// Stored on the Hub rather than in the browser, which is the whole reason
/// this type exists. A control surface is a tag on a wall and a phone that
/// has never seen this house before (docs/CONTROL-SURFACE.md); stations kept
/// in one browser's local storage would be invisible to every other way of
/// reaching the Hub, which is most of them.
///
/// No genre, no logo, no bitrate. Those come from a station directory, and
/// the directory is a later job — see ROADMAP.md. What a switchboard needs
/// is a label and something to play.
/// </remarks>
public sealed record Station
{
    /// <summary>Stable slug derived from the first name given. Never changes.</summary>
    public required string Id { get; init; }

    /// <summary>What the switchboard shows on the button.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// What the user pasted. Deliberately not resolved on the way in: a
    /// playlist URL is the durable address of a station and the stream URLs
    /// behind it move, so resolving is done afresh each time it is played
    /// (<see cref="StationPlaylist"/>).
    /// </summary>
    public required string Url { get; init; }
}
