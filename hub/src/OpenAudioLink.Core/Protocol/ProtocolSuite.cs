using System.Net;

namespace OpenAudioLink.Core.Protocol;

/// <summary>
/// Constants of the OpenAudioLink protocol suite. See protocol/README.md.
/// </summary>
public static class ProtocolSuite
{
    public const string Version = "0.1";

    public const int DiscoveryPort = 41000;
    public const int DeviceControlPort = 41001;
    public const int DefaultHubPort = 41080;

    /// <summary>Default RTP audio port (protocol/AUDIO-RTP.md).</summary>
    public const int RtpPort = 41100;

    public static readonly IPAddress DiscoveryMulticastGroup = IPAddress.Parse("239.255.41.10");

    /// <summary>
    /// A peer is compatible when its protocol-suite major version matches ours.
    /// Compatibility never depends on firmware or Hub version strings.
    /// </summary>
    public static bool IsCompatible(string? peerVersion)
    {
        if (string.IsNullOrEmpty(peerVersion))
        {
            return false;
        }

        var ourMajor = Version.AsSpan(0, Version.IndexOf('.'));
        var dot = peerVersion.IndexOf('.');
        var peerMajor = dot < 0 ? peerVersion.AsSpan() : peerVersion.AsSpan(0, dot);
        return peerMajor.SequenceEqual(ourMajor);
    }
}
