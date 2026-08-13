using System.Text;

namespace OpenAudioLink.Core.Radio;

/// <summary>
/// What a station's stream turns out to be, read from its first bytes.
/// </summary>
/// <remarks>
/// Here rather than inside the source that uses it because it is pure
/// decision-making over a handful of bytes, and because getting it wrong is
/// silent. A stream sent to the wrong decoder does not fail — it plays
/// nothing, for as long as the station is on, while every counter in the
/// system reports health. That is worth tests, and tests want it out of a
/// class that owns a socket and a thread.
///
/// The rule throughout: the bytes decide, and the Content-Type only breaks
/// a tie. Broadcasters get the header wrong in both directions — more than
/// one serves <c>audio/mpeg</c> for AAC — and a frame sync does not lie.
/// </remarks>
public static class StationCodec
{
    /// <summary>
    /// How much of the start of a stream to read before deciding.
    /// </summary>
    /// <remarks>
    /// Four bytes was enough for a signature sitting at byte zero and not
    /// enough for one behind an ID3 tag or a few bytes of server preamble —
    /// which is how a FLAC station came to be handed to the MP3 decoder and
    /// played silence for fifteen minutes. Reading more costs nothing: the
    /// sniffed bytes are served back to the decoder before anything else.
    /// </remarks>
    public const int SniffBytes = 64;

    /// <summary>
    /// Which codec this is, when it is one Media Foundation decodes.
    /// </summary>
    /// <remarks>
    /// Null means MP3, or something unrecognised — the two are separated by
    /// <see cref="LooksLikeMp3"/>, because "not one of ours" and "MP3" want
    /// very different handling and only one of them should reach a decoder.
    /// </remarks>
    public static string? MediaFoundation(string? contentType, ReadOnlySpan<byte> head)
    {
        // Searched for rather than tested at byte zero: a native FLAC stream
        // announces itself with "fLaC", but not necessarily first. A
        // four-byte magic turning up in this window by chance is not a risk
        // worth pricing against a station that will not play.
        if (head.IndexOf("fLaC"u8) >= 0)
        {
            return "FLAC";
        }

        // Ogg, which on a radio server is nearly always FLAC and
        // occasionally Vorbis. Named for what the container says, because
        // what is inside it is the decoder's problem rather than this
        // method's.
        if (head.IndexOf("OggS"u8) >= 0)
        {
            return "Ogg";
        }

        // MP3 and ADTS both open with a frame sync of set bits, and the two
        // bits after it separate them: MPEG audio carries a layer number
        // there and ADTS is required to leave it zero.
        if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xF0) == 0xF0)
        {
            return (head[1] & 0x06) == 0 ? "AAC" : null;
        }

        if (contentType is null)
        {
            return null;
        }
        if (contentType.Contains("aac", StringComparison.OrdinalIgnoreCase))
        {
            return "AAC";
        }
        if (contentType.Contains("flac", StringComparison.OrdinalIgnoreCase))
        {
            return "FLAC";
        }
        return contentType.Contains("ogg", StringComparison.OrdinalIgnoreCase) ? "Ogg" : null;
    }

    /// <summary>
    /// Whether the MP3 frame reader is worth pointing at this stream.
    /// </summary>
    /// <remarks>
    /// Permissive on purpose. It is not asking "is this MP3" — that is the
    /// decoder's job — but "is there any reason to think so", and the cost
    /// of a wrong "no" is a station that refuses to play when it could have.
    /// So an ID3 tag counts, and so does a frame sync anywhere in the window
    /// rather than only at the front: an Icecast server may start a listener
    /// mid-frame, and demanding a sync in byte zero would turn a working
    /// station into a broken one.
    ///
    /// The cost of a wrong "yes" is bounded elsewhere, by a limit on how far
    /// the decoder may search before it admits there is nothing there.
    /// </remarks>
    public static bool LooksLikeMp3(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("ID3"u8))
        {
            return true;
        }

        for (int i = 0; i + 1 < head.Length; i++)
        {
            // Eleven set bits: the sync every MPEG audio frame opens with,
            // and the only part of the header this needs to recognise.
            if (head[i] == 0xFF && (head[i + 1] & 0xE0) == 0xE0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The start of a stream, as something a person can read.</summary>
    /// <remarks>
    /// Hex and text together, because either alone loses the answer: a
    /// signature like <c>fLaC</c>, or an HTML error page served instead of
    /// audio, is obvious as text and unreadable as hex; a frame sync is the
    /// other way round.
    ///
    /// This exists because the first bytes of a stream are the one thing
    /// nobody can see from outside the Hub, and they are exactly what is
    /// needed when a station will not play.
    /// </remarks>
    public static string Describe(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0)
        {
            return "nothing at all — the station accepted the connection and sent no bytes";
        }

        int shown = Math.Min(head.Length, 16);
        var hex = new StringBuilder(shown * 3);
        var text = new StringBuilder(shown);

        for (int i = 0; i < shown; i++)
        {
            hex.Append(head[i].ToString("X2")).Append(' ');
            text.Append(head[i] is >= 0x20 and < 0x7F ? (char)head[i] : '.');
        }

        return $"{hex.ToString().TrimEnd()} (\"{text}\")";
    }
}
