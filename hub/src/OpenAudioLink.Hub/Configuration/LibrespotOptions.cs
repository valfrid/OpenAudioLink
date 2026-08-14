using OpenAudioLink.Core.Audio;

namespace OpenAudioLink.Hub.Configuration;

/// <summary>
/// How the Hub runs librespot (docs/LIBRESPOT.md).
///
/// The binary is not shipped with the Hub. Whether a particular
/// reimplementation is licensed for a particular service is the operator's
/// decision, so the Hub locates and manages a binary they supplied rather
/// than bundling one — docs/CAST-POINTS.md. Everything here has a working
/// default except that path.
/// </summary>
public sealed class LibrespotOptions
{
    public const string SectionName = "Librespot";

    /// <summary>
    /// Turns the whole feature off without removing the binary. A Hub with
    /// no librespot installed behaves as if this were false, so the default
    /// costs nothing.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Path to the librespot executable, or a bare name to be found on
    /// PATH. Empty means "look for librespot next to the Hub, then on
    /// PATH", which covers both the operator who dropped it in the folder
    /// and the one who installed it properly.
    /// </summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>
    /// What Spotify shows next to the name. "speaker" is what a speaker is.
    /// </summary>
    public string DeviceType { get; set; } = "speaker";

    /// <summary>
    /// Sample layout on the pipe. F32 is what librespot decodes to
    /// internally, so asking for it skips a quantisation step the Hub would
    /// only have to undo — everything downstream of here is float until the
    /// packetiser.
    /// </summary>
    public string Format { get; set; } = "F32";

    /// <summary>96, 160 or 320. Anything else librespot rejects at startup.</summary>
    public int Bitrate { get; set; } = 320;

    /// <summary>
    /// Percent. Volume set on the phone is applied by librespot before the
    /// pipe, so it reaches the speakers without the Hub doing anything —
    /// which is the parity docs/CAST-POINTS.md asks for.
    /// </summary>
    public int InitialVolume { get; set; } = 50;

    /// <summary>
    /// Seconds of silence that end a stream. Long enough not to cut the gap
    /// between two tracks, short enough that a room does not sit there
    /// receiving silence all evening.
    /// </summary>
    public int IdleSeconds { get; set; } = 5;

    /// <summary>
    /// Which of this host's IPv4 addresses librespot announces on.
    ///
    /// Empty means the Hub works it out: the address sharing a subnet with
    /// a known speaker, which is by definition an address the phone can
    /// reach too. Set it only to override that.
    ///
    /// This exists because leaving the choice to the operating system is
    /// wrong on any machine with a VPN. librespot's own default is "all
    /// interfaces", and a socket bound that way lets Windows pick where
    /// multicast goes — by route metric. On the machine this was found on,
    /// Tailscale sat at metric 5 against Wi-Fi's 50, so the announcements
    /// went out over the overlay to nobody while the speakers, the phone
    /// and the Hub all sat on the LAN wondering. It is the same trap
    /// <see cref="OpenAudioLink.Core.Net.LocalAddressSelector"/> was
    /// written for, one layer down.
    /// </summary>
    public string ZeroconfInterface { get; set; } = "";

    /// <summary>
    /// Whether the phone's volume is the speaker's volume, rather than a
    /// second attenuator in front of it.
    /// </summary>
    /// <remarks>
    /// Decision 14 says volume is digital gain at the Consumer. librespot
    /// arrived before that decision and has always applied the phone's
    /// volume to the samples itself, so a Spotify stream passed through two
    /// attenuators where every other source passes through one. They
    /// multiply: a phone at half and a speaker at 38 % is -43 dB, and on a
    /// powered cabinet that shuts down below a signal threshold the result
    /// is not quiet, it is silent.
    ///
    /// On, librespot is run with <c>--volume-ctrl fixed</c> and hands over
    /// samples untouched, while the phone's volume changes are forwarded to
    /// the speaker. One gain stage, one control, and the highest analogue
    /// level a given loudness allows.
    ///
    /// Off restores the old behaviour, which is worth keeping reachable:
    /// this depends on two librespot options and on what <c>fixed</c> means
    /// in a particular build, and a Hub that cannot make a sound is worse
    /// than one whose volume is awkward.
    /// </remarks>
    public bool UnifiedVolume { get; set; } = true;

    /// <summary>
    /// Extra arguments appended verbatim, for anything this class does not
    /// model. Split on spaces, so an argument containing one needs the
    /// operator to think of another way.
    /// </summary>
    public string ExtraArguments { get; set; } = "";

    /// <summary>
    /// Where librespot keeps credentials and its audio cache. Empty means a
    /// per-cast-point folder under the Hub's data directory. Credentials
    /// must persist, or every restart makes the phone log in again.
    /// </summary>
    public string CacheDirectory { get; set; } = "";

    /// <summary>The sample rate librespot emits. Spotify is 44.1 kHz, always.</summary>
    public const int SourceSampleRate = 44100;

    /// <summary>librespot's pipe backend is always stereo.</summary>
    public const int SourceChannels = 2;

    public PcmSampleFormat SampleFormat =>
        PcmDecoder.TryParse(Format, out var parsed) ? parsed : PcmSampleFormat.F32;

    /// <summary>
    /// Whether <see cref="Format"/> names something we can decode. F64 and
    /// S24 are valid librespot formats this Hub does not read, and saying so
    /// at startup beats a stream of noise.
    /// </summary>
    public bool HasUsableFormat => PcmDecoder.TryParse(Format, out _);
}
