using System.Text;

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
///
/// The id is fixed at creation and survives renaming, because it is what
/// persistence and the API refer to. Renaming "Kitchen" to "Kök" must not
/// create a second cast point or orphan the first.
/// </summary>
public static class CastPointId
{
    public const int MaxLength = 48;

    /// <summary>
    /// Lowercase, ASCII letters and digits, single hyphens between words.
    /// Returns null when a name contains nothing usable — which is worth
    /// distinguishing from an empty name, since "🎵" is a name a person
    /// might reasonably type and cannot become an id.
    /// </summary>
    public static string? FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var slug = new StringBuilder(MaxLength);
        var pendingHyphen = false;

        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            if (slug.Length >= MaxLength)
            {
                break;
            }

            // FormD splits "ö" into "o" plus a combining mark, so folding
            // Scandinavian and other accented names falls out of dropping
            // anything non-alphanumeric rather than needing a table.
            if (char.IsAsciiLetterOrDigit(ch))
            {
                if (pendingHyphen && slug.Length > 0)
                {
                    slug.Append('-');
                }
                slug.Append(char.ToLowerInvariant(ch));
                pendingHyphen = false;
            }
            else if (slug.Length > 0)
            {
                pendingHyphen = true;
            }
        }

        return slug.Length > 0 ? slug.ToString() : null;
    }

    /// <summary>
    /// Appends a numeric suffix until the id is free. Two rooms both called
    /// "Bedroom" is an ordinary thing to want, and refusing the second is a
    /// worse answer than naming it "bedroom-2".
    /// </summary>
    public static string MakeUnique(string candidate, Func<string, bool> taken)
    {
        if (!taken(candidate))
        {
            return candidate;
        }

        var stem = candidate.Length + 3 > MaxLength
            ? candidate[..(MaxLength - 3)].TrimEnd('-')
            : candidate;

        for (var suffix = 2; ; suffix++)
        {
            var next = $"{stem}-{suffix}";
            if (!taken(next))
            {
                return next;
            }
        }
    }
}
