using System.Globalization;
using System.Text;

namespace OpenAudioLink.Core;

/// <summary>
/// Turns a display name into a stable, URL-safe id.
///
/// The id is fixed when a thing is created and survives renaming, because it
/// is what persistence and the API refer to. Renaming "Kitchen" to "Kök" must
/// not create a second one or orphan the first.
/// </summary>
/// <remarks>
/// Written for cast points and reused by stations. Kept general because
/// there is nothing about it that is specific to either: both are things a
/// person names and the Hub then has to refer to by an id that does not
/// change under them.
/// </remarks>
public static class Slug
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

            // FormD splits "ö" into "o" plus a combining diaeresis, which
            // folds accented names without a translation table — but only
            // if the mark is discarded rather than treated as a separator.
            // Left as a separator it turns "Kök" into "ko-k".
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

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
