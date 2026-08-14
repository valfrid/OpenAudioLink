using System.Text;
using OpenAudioLink.Core.Radio;
using Xunit;

namespace OpenAudioLink.Core.Tests;

/// <summary>
/// The sniff that decides which decoder a station goes to.
/// </summary>
/// <remarks>
/// Written after it sent a FLAC station to the MP3 decoder, which played
/// nothing for fifteen minutes while every counter in the system reported
/// health. There was no test here at the time, and the failure mode is
/// silence — so the cases below are mostly the shapes of streams that have
/// actually turned up, not invented ones.
/// </remarks>
public class StationCodecTests
{
    private static byte[] Stream(params byte[] head) => Pad(head);

    /// <summary>A realistic-length window, since the sniff reads 64 bytes.</summary>
    private static byte[] Pad(byte[] head)
    {
        var buffer = new byte[StationCodec.SniffBytes];
        head.CopyTo(buffer, 0);
        return buffer;
    }

    private static byte[] Ascii(string text, int offset = 0)
    {
        var buffer = new byte[StationCodec.SniffBytes];
        Encoding.ASCII.GetBytes(text).CopyTo(buffer, offset);
        return buffer;
    }

    [Fact]
    public void NativeFlacIsRecognisedFromItsSignature()
    {
        Assert.Equal("FLAC", StationCodec.MediaFoundation(null, Ascii("fLaC")));
    }

    /// <summary>
    /// The case that caused the bug: the signature is there, just not first.
    /// </summary>
    [Fact]
    public void FlacIsFoundBehindAPreamble()
    {
        Assert.Equal("FLAC", StationCodec.MediaFoundation(null, Ascii("fLaC", offset: 11)));
    }

    [Fact]
    public void OggIsRecognisedFromItsSignature()
    {
        Assert.Equal("Ogg", StationCodec.MediaFoundation(null, Ascii("OggS")));
    }

    /// <summary>ADTS: frame sync, then layer bits left at zero.</summary>
    [Fact]
    public void AacIsRecognisedFromItsFrameSync()
    {
        Assert.Equal("AAC", StationCodec.MediaFoundation(null, Stream(0xFF, 0xF1)));
    }

    /// <summary>
    /// The same sync with a layer number in it is MPEG audio, not ADTS, and
    /// must not be routed away from the MP3 decoder.
    /// </summary>
    [Fact]
    public void Mp3IsNotMistakenForAac()
    {
        Assert.Null(StationCodec.MediaFoundation(null, Stream(0xFF, 0xFB)));
    }

    /// <summary>
    /// Broadcasters serve audio/mpeg for AAC. The bytes win.
    /// </summary>
    [Fact]
    public void TheBytesBeatAWrongContentType()
    {
        Assert.Equal("FLAC", StationCodec.MediaFoundation("audio/mpeg", Ascii("fLaC")));
        Assert.Null(StationCodec.MediaFoundation("audio/aac", Stream(0xFF, 0xFB)));
    }

    /// <summary>
    /// The header only breaks a tie — when there is no signature to read.
    /// </summary>
    [Theory]
    [InlineData("audio/aac", "AAC")]
    [InlineData("audio/flac", "FLAC")]
    [InlineData("application/ogg", "Ogg")]
    [InlineData("audio/mpeg", null)]
    [InlineData("text/html", null)]
    public void TheContentTypeDecidesWhenTheBytesSayNothing(string contentType, string? expected)
    {
        Assert.Equal(expected, StationCodec.MediaFoundation(contentType, new byte[StationCodec.SniffBytes]));
    }

    [Fact]
    public void AnUnknownStreamWithNoContentTypeIsUnknown()
    {
        Assert.Null(StationCodec.MediaFoundation(null, new byte[StationCodec.SniffBytes]));
    }

    [Fact]
    public void AnId3TagLooksLikeMp3()
    {
        Assert.True(StationCodec.LooksLikeMp3(Ascii("ID3")));
    }

    [Fact]
    public void AFrameSyncLooksLikeMp3()
    {
        Assert.True(StationCodec.LooksLikeMp3(Stream(0xFF, 0xFB, 0x90, 0x00)));
    }

    /// <summary>
    /// A listener joined mid-frame still gets to play, which is why a header
    /// is searched for rather than demanded at byte zero.
    /// </summary>
    [Fact]
    public void AFrameHeaderPartwayInStillLooksLikeMp3()
    {
        var head = new byte[StationCodec.SniffBytes];
        head[20] = 0xFF;
        head[21] = 0xFB;
        head[22] = 0x90;
        Assert.True(StationCodec.LooksLikeMp3(head));
    }

    /// <summary>
    /// The bug this whole class exists to prevent, and the one a sync-only
    /// check could not catch: a FLAC frame header is eleven set bits too.
    /// Its layer bits are 00, which MPEG reserves and no MP3 frame may use.
    /// </summary>
    [Theory]
    [InlineData(0xF8)]
    [InlineData(0xF9)]
    public void AFlacFrameHeaderDoesNotLookLikeMp3(int second)
    {
        Assert.False(StationCodec.LooksLikeMp3(Stream(0xFF, (byte)second, 0x90)));
    }

    /// <summary>Free-format and invalid bitrates are not broadcast.</summary>
    [Theory]
    [InlineData(0x00)]
    [InlineData(0xF0)]
    public void AnImpossibleBitrateDoesNotLookLikeMp3(int third)
    {
        Assert.False(StationCodec.LooksLikeMp3(Stream(0xFF, 0xFB, (byte)third)));
    }

    [Fact]
    public void AReservedSampleRateDoesNotLookLikeMp3()
    {
        Assert.False(StationCodec.LooksLikeMp3(Stream(0xFF, 0xFB, 0x9C)));
    }

    [Fact]
    public void AReservedVersionDoesNotLookLikeMp3()
    {
        Assert.False(StationCodec.LooksLikeMp3(Stream(0xFF, 0xEB, 0x90)));
    }

    /// <summary>
    /// Ogg carries several codecs and this project decodes one of them.
    /// Naming the other is the difference between "wrong entry in the
    /// station's format list" and "the Hub is broken".
    /// </summary>
    [Fact]
    public void OggFlacIsNamedFromItsMappingHeader()
    {
        var head = new byte[StationCodec.SniffBytes];
        Encoding.ASCII.GetBytes("OggS").CopyTo(head, 0);
        head[28] = 0x7F;
        Encoding.ASCII.GetBytes("FLAC").CopyTo(head, 29);
        Assert.Equal("FLAC", StationCodec.OggPayload(head));
    }

    [Fact]
    public void OggVorbisIsNamed()
    {
        var head = new byte[StationCodec.SniffBytes];
        Encoding.ASCII.GetBytes("OggS").CopyTo(head, 0);
        head[28] = 0x01;
        Encoding.ASCII.GetBytes("vorbis").CopyTo(head, 29);
        Assert.Equal("Vorbis", StationCodec.OggPayload(head));
    }

    [Fact]
    public void OggOpusIsNamed()
    {
        var head = new byte[StationCodec.SniffBytes];
        Encoding.ASCII.GetBytes("OggS").CopyTo(head, 0);
        Encoding.ASCII.GetBytes("OpusHead").CopyTo(head, 28);
        Assert.Equal("Opus", StationCodec.OggPayload(head));
    }

    /// <summary>A stream that is not Ogg has no Ogg payload to name.</summary>
    [Fact]
    public void ANonOggStreamHasNoOggPayload()
    {
        Assert.Null(StationCodec.OggPayload(Ascii("fLaC")));
    }

    /// <summary>
    /// A URL is never allowed to route a stream, only to name one — but it
    /// has to name it, because some stations cannot be identified at all.
    /// </summary>
    [Theory]
    [InlineData("http://stream.radioparadise.com/flac", "FLAC")]
    [InlineData("http://live1.sr.se/p1-aac-320", "AAC")]
    [InlineData("http://example.com/stream.ogg", "Ogg")]
    [InlineData("http://http-live.sr.se/p3-mp3-192", null)]
    public void TheUrlNamesAStreamOfLastResort(string url, string? expected)
    {
        Assert.Equal(expected, StationCodec.FromUrl(url));
    }

    /// <summary>
    /// The whole point: FLAC must not be handed to the MP3 decoder, because
    /// on a live stream that decoder can never report failure.
    /// </summary>
    [Fact]
    public void FlacDoesNotLookLikeMp3()
    {
        Assert.False(StationCodec.LooksLikeMp3(Ascii("fLaC")));
    }

    [Fact]
    public void AnErrorPageDoesNotLookLikeMp3()
    {
        Assert.False(StationCodec.LooksLikeMp3(Ascii("<!DOCTYPE html><html><head>")));
    }

    [Fact]
    public void DescribeShowsBothHexAndText()
    {
        var described = StationCodec.Describe("fLaC"u8);
        Assert.Contains("66 4C 61 43", described);
        Assert.Contains("fLaC", described);
    }

    /// <summary>
    /// A station that connects and sends nothing is its own diagnosis, and
    /// an empty hex dump would not say so.
    /// </summary>
    [Fact]
    public void DescribeSaysSoWhenThereIsNothingToDescribe()
    {
        Assert.Contains("nothing at all", StationCodec.Describe(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void DescribeStopsAtSixteenBytes()
    {
        var described = StationCodec.Describe(new byte[StationCodec.SniffBytes]);
        Assert.Equal(16, described.Split(' ').Count(part => part == "00"));
    }
}
