namespace OpenAudioLink.Core.Radio;

/// <summary>What a URL turned out to be pointing at.</summary>
public enum StationKind
{
    /// <summary>An endless Icecast or Shoutcast stream. What we can play.</summary>
    Stream,

    /// <summary>
    /// A segment playlist — HLS or DASH. Deliberately not supported.
    /// </summary>
    /// <remarks>
    /// The BBC and most commercial broadcasters left single endless streams
    /// for playlists of short files, refetched continuously, with
    /// discontinuities to handle. That is a client, not a fetch, and
    /// pretending otherwise fails in a way that looks like a decoder bug.
    /// Naming it is the whole point of this value.
    /// </remarks>
    SegmentPlaylist,

    /// <summary>Nothing usable was found.</summary>
    Unusable,
}

/// <summary>The result of resolving whatever a user pasted.</summary>
/// <param name="Kind">What it turned out to be.</param>
/// <param name="Urls">
/// Stream URLs in the order the playlist gave them, which is the order to
/// try: stations list mirrors, and the first is the one they prefer.
/// </param>
public readonly record struct StationTarget(StationKind Kind, IReadOnlyList<string> Urls);

/// <summary>
/// Turns a pasted radio URL into the stream URLs behind it.
/// </summary>
/// <remarks>
/// Half of what people call a "stream URL" is a few lines of text naming
/// the real one — a <c>.pls</c> from Shoutcast, an <c>.m3u</c> from almost
/// everyone else. Resolving that is trivial and skipping it is not: without
/// it the first URL anyone tries fails inside the decoder, which is the
/// worst place to discover a text file.
///
/// Deliberately content-based rather than extension-based. Stations serve
/// <c>.m3u</c> from URLs ending in <c>.pls</c> and vice versa, and plenty
/// serve a playlist from a URL with no extension at all.
/// </remarks>
public static class StationPlaylist
{
    /// <summary>
    /// Reads a fetched document. Returns <see cref="StationKind.Stream"/>
    /// with the URLs it names, or says why it cannot.
    /// </summary>
    /// <param name="content">The fetched body, as text.</param>
    public static StationTarget Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new StationTarget(StationKind.Unusable, []);
        }

        var lines = content.Split('\n', '\r');

        /*
         * HLS first, because an HLS playlist is a valid m3u and would
         * otherwise parse into a list of segment files that play for four
         * seconds each and then stop. The tags below are what distinguish
         * a segment playlist from a station list; either the media tags or
         * a variant list is enough.
         */
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-", StringComparison.OrdinalIgnoreCase))
            {
                return new StationTarget(StationKind.SegmentPlaylist, []);
            }
        }

        var urls = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // A .pls entry: File1=http://..., possibly with spaces around
            // the equals sign. Title and Length lines are ignored.
            if (line.StartsWith("File", StringComparison.OrdinalIgnoreCase))
            {
                var equals = line.IndexOf('=');
                if (equals > 0)
                {
                    Add(urls, line[(equals + 1)..].Trim());
                }
                continue;
            }

            // Comments in either format, including #EXTINF track titles.
            if (line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            // Anything else in a .m3u is a URL, and a bare URL is itself.
            Add(urls, line);
        }

        return urls.Count > 0
            ? new StationTarget(StationKind.Stream, urls)
            : new StationTarget(StationKind.Unusable, []);
    }

    /// <summary>
    /// True when a fetched body should be parsed rather than played.
    /// </summary>
    /// <remarks>
    /// Audio arrives as bytes and a playlist as text, so the content type
    /// decides — and a stream must never be read as text, because reading
    /// even the first line of one consumes audio and blocks until it
    /// arrives. Unknown types are treated as audio: a station serving
    /// <c>application/octet-stream</c> is common, a playlist doing so is
    /// not.
    /// </remarks>
    public static bool LooksLikePlaylist(string? contentType, string? url)
    {
        if (contentType is not null)
        {
            var type = contentType.Split(';')[0].Trim();

            if (type.Equals("audio/x-mpegurl", StringComparison.OrdinalIgnoreCase)
                || type.Equals("audio/mpegurl", StringComparison.OrdinalIgnoreCase)
                || type.Equals("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase)
                || type.Equals("audio/x-scpls", StringComparison.OrdinalIgnoreCase)
                || type.Equals("application/pls+xml", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Everything else that announces itself as audio is audio.
            if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                || type.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // No useful type. Fall back to the extension, which is a hint and
        // not a promise — hence trying the content type first.
        if (url is null)
        {
            return false;
        }

        var path = url.Split('?')[0].Split('#')[0];
        return path.EndsWith(".pls", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    private static void Add(List<string> urls, string candidate)
    {
        // Absolute http(s) only. A playlist may name a relative path or a
        // local file, and neither is something to hand to an HTTP client
        // without saying so.
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !urls.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            urls.Add(candidate);
        }
    }
}
