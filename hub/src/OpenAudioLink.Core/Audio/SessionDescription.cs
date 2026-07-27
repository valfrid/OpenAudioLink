using System.Text;

namespace OpenAudioLink.Core.Audio;

/// <summary>
/// Builds the SDP document (RFC 4566) that describes a stream. This is
/// what lets receivers that know nothing about OpenAudioLink — ffplay,
/// VLC, an AES67 device — decode it.
/// </summary>
public static class SessionDescription
{
    public static string Build(
        AudioStreamFormat format,
        string originAddress,
        string destinationAddress,
        int destinationPort,
        byte payloadType = 96,
        string sessionName = "OpenAudioLink")
    {
        ArgumentNullException.ThrowIfNull(format);
        format.Validate();

        var sdp = new StringBuilder();
        sdp.Append("v=0\r\n");
        sdp.Append($"o=- 1 1 IN IP4 {originAddress}\r\n");
        sdp.Append($"s={sessionName}\r\n");
        sdp.Append($"c=IN IP4 {destinationAddress}\r\n");
        sdp.Append("t=0 0\r\n");
        sdp.Append($"m=audio {destinationPort} RTP/AVP {payloadType}\r\n");
        sdp.Append($"a=rtpmap:{payloadType} {format.RtpmapName}/{format.SampleRate}/{format.Channels}\r\n");
        sdp.Append($"a=ptime:{format.PacketMilliseconds}\r\n");
        sdp.Append("a=recvonly\r\n");
        return sdp.ToString();
    }
}
