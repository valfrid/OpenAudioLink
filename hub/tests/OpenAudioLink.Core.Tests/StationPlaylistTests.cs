using OpenAudioLink.Core.Radio;
using Xunit;

namespace OpenAudioLink.Core.Tests;

public class StationPlaylistTests
{
    [Fact]
    public void A_shoutcast_pls_names_its_stream()
    {
        var target = StationPlaylist.Parse("""
            [playlist]
            NumberOfEntries=1
            File1=http://ice1.somafm.com/groovesalad-256-mp3
            Title1=SomaFM: Groove Salade
            Length1=-1
            Version=2
            """);

        Assert.Equal(StationKind.Stream, target.Kind);
        Assert.Equal(["http://ice1.somafm.com/groovesalad-256-mp3"], target.Urls);
    }

    [Fact]
    public void An_m3u_keeps_the_order_it_listed()
    {
        // Stations list mirrors, and the first is the one they prefer.
        var target = StationPlaylist.Parse("""
            #EXTM3U
            #EXTINF:-1,Radio Paradise
            http://stream.radioparadise.com/flac
            http://stream-uk.radioparadise.com/flac
            """);

        Assert.Equal(StationKind.Stream, target.Kind);
        Assert.Equal(
            ["http://stream.radioparadise.com/flac", "http://stream-uk.radioparadise.com/flac"],
            target.Urls);
    }

    [Fact]
    public void A_bare_url_is_its_own_answer()
    {
        var target = StationPlaylist.Parse("https://ice.example/stream");

        Assert.Equal(StationKind.Stream, target.Kind);
        Assert.Equal(["https://ice.example/stream"], target.Urls);
    }

    /// <summary>
    /// The case that matters most, because an HLS playlist is a valid m3u:
    /// parsed as a station list it yields segment files that play for a few
    /// seconds each and then stop, which looks like a decoder fault rather
    /// than an unsupported protocol.
    /// </summary>
    [Fact]
    public void An_hls_playlist_is_recognised_rather_than_half_played()
    {
        var media = StationPlaylist.Parse("""
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:6
            #EXTINF:6.0,
            segment-1.aac
            #EXTINF:6.0,
            segment-2.aac
            """);
        Assert.Equal(StationKind.SegmentPlaylist, media.Kind);
        Assert.Empty(media.Urls);

        // A variant playlist names absolute URLs and would otherwise look
        // exactly like a station list with several mirrors.
        var variants = StationPlaylist.Parse("""
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=128000
            https://example/low/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=320000
            https://example/high/index.m3u8
            """);
        Assert.Equal(StationKind.SegmentPlaylist, variants.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \n")]
    [InlineData(null)]
    [InlineData("[playlist]\nNumberOfEntries=0\nVersion=2")]
    public void Nothing_usable_says_so(string? content) =>
        Assert.Equal(StationKind.Unusable, StationPlaylist.Parse(content).Kind);

    [Fact]
    public void Relative_and_local_entries_are_refused()
    {
        // A playlist may name a path or a file, and neither is something to
        // hand to an HTTP client without noticing.
        var target = StationPlaylist.Parse("""
            #EXTM3U
            /relative/path.mp3
            file:///home/someone/music.mp3
            C:\music\track.mp3
            http://good.example/stream
            """);

        Assert.Equal(["http://good.example/stream"], target.Urls);
    }

    [Fact]
    public void The_same_url_twice_is_listed_once()
    {
        var target = StationPlaylist.Parse("""
            [playlist]
            File1=http://ice.example/stream
            File2=http://ice.example/stream
            """);

        Assert.Single(target.Urls);
    }

    [Theory]
    [InlineData("audio/x-scpls", "http://example/listen", true)]
    [InlineData("audio/x-mpegurl", "http://example/listen", true)]
    [InlineData("application/vnd.apple.mpegurl", "http://example/x", true)]
    [InlineData("audio/mpeg", "http://example/listen.pls", false)]
    [InlineData("audio/aacp", "http://example/listen", false)]
    [InlineData("application/octet-stream", "http://example/listen", false)]
    public void The_content_type_decides_before_the_extension(
        string contentType, string url, bool expected) =>
        Assert.Equal(expected, StationPlaylist.LooksLikePlaylist(contentType, url));

    [Theory]
    [InlineData("http://example/station.pls", true)]
    [InlineData("http://example/station.m3u?x=1", true)]
    [InlineData("http://example/station.m3u8#frag", true)]
    [InlineData("http://example/stream", false)]
    public void Without_a_content_type_the_extension_is_the_hint(string url, bool expected) =>
        Assert.Equal(expected, StationPlaylist.LooksLikePlaylist(null, url));

    /// <summary>
    /// A stream must never be read as text: reading even its first line
    /// consumes audio and blocks until enough of it arrives. So anything
    /// claiming to be audio is audio, whatever the URL ends in.
    /// </summary>
    [Fact]
    public void An_audio_content_type_beats_a_playlist_extension() =>
        Assert.False(StationPlaylist.LooksLikePlaylist("audio/mpeg", "http://example/listen.m3u"));
}
