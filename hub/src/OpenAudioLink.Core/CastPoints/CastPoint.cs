namespace OpenAudioLink.Core.CastPoints;

/// <summary>
/// A named place to send audio: "Kitchen", "Living room", "House"
/// (docs/CAST-POINTS.md).
///
/// A zone and a group are the same object. A cast point with one consumer
/// is a room; one with twelve is the whole house. Nothing distinguishes
/// them, because nothing needs to: the Producer already replicates one
/// byte-identical packet to however many destinations it is given, so a
/// group is not a different kind of thing at the transport layer.
/// </summary>
public sealed record CastPoint
{
    /// <summary>Stable slug derived from the first name given. Never changes.</summary>
    public required string Id { get; init; }

    /// <summary>What a phone shows in its device picker.</summary>
    public required string Name { get; init; }

    /// <summary>Consumer device ids. May be empty while a room is being set up.</summary>
    public required IReadOnlyList<string> Destinations { get; init; }
}

/// <summary>
/// Derives the stable id from a display name.
/// </summary>
/// <remarks>
/// A cast point's own name for <see cref="Slug"/>, kept because the id is
/// part of the API and reading <c>CastPointId.FromName</c> at the call site
/// says which id is being made.
/// </remarks>
public static class CastPointId
{
    public const int MaxLength = Slug.MaxLength;

    /// <inheritdoc cref="Slug.FromName"/>
    public static string? FromName(string? name) => Slug.FromName(name);

    /// <inheritdoc cref="Slug.MakeUnique"/>
    public static string MakeUnique(string candidate, Func<string, bool> taken) =>
        Slug.MakeUnique(candidate, taken);
}
